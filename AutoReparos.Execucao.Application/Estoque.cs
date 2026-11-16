using System.Text.Json;
using AutoReparos.Execucao.Domain;

namespace AutoReparos.Execucao.Application;

public sealed record ItemReserva(Guid InsumoId, int Quantidade);
public sealed record ItemEmFalta(Guid InsumoId, int QuantidadeSolicitada, int QuantidadeDisponivel);

public enum ResultadoReserva { Reservada, SemSaldo, Duplicada }
public enum ResultadoCancelamentoReserva { Cancelada, Duplicada }

public sealed record EventoReservarInsumos(Guid EventId, Guid OrdemServicoId, Guid? CorrelacaoId, IReadOnlyList<ItemReserva> Itens)
{
    public const int ItensMaximos = 200;

    // Comando do OS Service (camelCase): eventId, tipo, ordemServicoId, itens [{insumoId, quantidade}] e, opcional, correlacaoId.
    public static bool TentarLer(string? json, out EventoReservarInsumos? evento)
    {
        evento = null;
        if (!LeitorJson.TentarLerObjeto(json, out var corpo)) return false;
        if (!LeitorJson.TentarLerTexto(corpo, "tipo", 100, out var tipo) || tipo != TiposEvento.ReservarInsumos) return false;
        if (!LeitorJson.TentarLerGuid(corpo, "eventId", out var eventId) || eventId == Guid.Empty
            || !LeitorJson.TentarLerGuid(corpo, "ordemServicoId", out var ordemId) || ordemId == Guid.Empty
            || !LeitorJson.TentarLerGuidOpcional(corpo, "correlacaoId", out var correlacaoId)
            || !LeitorJson.Propriedade(corpo, "itens", out var lista) || lista.ValueKind != JsonValueKind.Array) return false;
        var itens = new List<ItemReserva>();
        foreach (var elemento in lista.EnumerateArray())
        {
            if (elemento.ValueKind != JsonValueKind.Object || !LeitorJson.TentarLerGuid(elemento, "insumoId", out var insumoId) || insumoId == Guid.Empty
                || !LeitorJson.Propriedade(elemento, "quantidade", out var q) || q.ValueKind != JsonValueKind.Number
                || !q.TryGetInt32(out var quantidade) || quantidade is < 1 or > LimitesCatalogo.QuantidadeMaxima) return false;
            itens.Add(new ItemReserva(insumoId, quantidade));
        }
        if (itens.Count is 0 or > ItensMaximos) return false;
        // A soma por insumo (itens repetidos) também respeita o limite da reserva.
        if (itens.GroupBy(i => i.InsumoId).Any(g => g.Sum(i => (long)i.Quantidade) > LimitesCatalogo.QuantidadeMaxima)) return false;
        evento = new EventoReservarInsumos(eventId, ordemId, correlacaoId, itens);
        return true;
    }
}

public sealed record EventoCancelarReserva(Guid EventId, Guid OrdemServicoId, Guid? CorrelacaoId)
{
    // Comando do OS Service (camelCase): eventId, tipo, ordemServicoId e, opcional, correlacaoId.
    public static bool TentarLer(string? json, out EventoCancelarReserva? evento)
    {
        evento = null;
        if (!LeitorJson.TentarLerObjeto(json, out var corpo)) return false;
        if (!LeitorJson.TentarLerTexto(corpo, "tipo", 100, out var tipo) || tipo != TiposEvento.CancelarReserva) return false;
        if (!LeitorJson.TentarLerGuid(corpo, "eventId", out var eventId) || eventId == Guid.Empty
            || !LeitorJson.TentarLerGuid(corpo, "ordemServicoId", out var ordemId) || ordemId == Guid.Empty
            || !LeitorJson.TentarLerGuidOpcional(corpo, "correlacaoId", out var correlacaoId)) return false;
        evento = new EventoCancelarReserva(eventId, ordemId, correlacaoId);
        return true;
    }
}

// Catálogo de serviços, insumos e reserva em duas fases. Cada passo grava negócio, Inbox e Outbox numa única transação (L-01).
public sealed class EstoqueService(IExecucaoStore store, TimeProvider relogio)
{
    private const int TentativasMaximas = 3;

    public async Task<ResultadoReserva> ReservarAsync(EventoReservarInsumos evento, CancellationToken ct)
    {
        for (var tentativa = 1; ; tentativa++)
        {
            if (await store.EventoProcessadoAsync(evento.EventId, ct)) return ResultadoReserva.Duplicada;
            var agora = relogio.GetUtcNow();
            await store.BloquearOrdemAsync(evento.OrdemServicoId, ct);
            var existentes = await store.ListarReservasAsync(evento.OrdemServicoId, ct);
            store.Adicionar(new EventoProcessado { EventId = evento.EventId, ProcessadoEm = agora });
            ResultadoReserva resultado;
            if (existentes.Count > 0)
            {
                // A OS já tem reserva (comando repetido ou de outro eventId): nada é reservado de novo; a Saga recebe o mesmo desfecho.
                var ativas = existentes.Where(r => r.Status != StatusReserva.Cancelada).ToList();
                store.Adicionar(ativas.Count > 0
                    ? Resposta(TiposEvento.InsumosReservados, evento.CorrelacaoId, evento.OrdemServicoId, ("itens", Itens(ativas)))
                    : Resposta(TiposEvento.InsumosReservaFalhou, evento.CorrelacaoId, evento.OrdemServicoId, ("motivo", "Reserva da OS já foi cancelada."), ("itensEmFalta", Array.Empty<object>())));
                resultado = ResultadoReserva.Duplicada;
            }
            else
            {
                // Itens repetidos do mesmo insumo viram uma linha; ordem estável evita deadlock entre reservas concorrentes.
                var itens = evento.Itens.GroupBy(i => i.InsumoId).Select(g => new ItemReserva(g.Key, g.Sum(i => i.Quantidade))).OrderBy(i => i.InsumoId).ToList();
                var faltas = await store.ReservarSaldoAsync(itens, ct);
                if (faltas.Count == 0)
                {
                    foreach (var item in itens) store.Adicionar(new ReservaInsumo(evento.OrdemServicoId, item.InsumoId, item.Quantidade, agora));
                    store.Adicionar(Resposta(TiposEvento.InsumosReservados, evento.CorrelacaoId, evento.OrdemServicoId,
                        ("itens", itens.Select(i => new { insumoId = i.InsumoId, quantidade = i.Quantidade }).ToArray())));
                    resultado = ResultadoReserva.Reservada;
                }
                else
                {
                    store.Adicionar(Resposta(TiposEvento.InsumosReservaFalhou, evento.CorrelacaoId, evento.OrdemServicoId, ("motivo", "Saldo insuficiente."),
                        ("itensEmFalta", faltas.Select(f => new { insumoId = f.InsumoId, quantidadeSolicitada = f.QuantidadeSolicitada, quantidadeDisponivel = f.QuantidadeDisponivel }).ToArray())));
                    resultado = ResultadoReserva.SemSaldo;
                }
            }
            try { await store.SalvarAsync(ct); }
            catch (ConcorrenciaException) when (tentativa < TentativasMaximas) { store.DescartarAlteracoes(); continue; }
            return resultado;
        }
    }

    // Compensação: devolve o saldo das reservas `Reservada`; `Confirmada` permanece. Sem reserva alguma, só confirma à Saga.
    public async Task<ResultadoCancelamentoReserva> CancelarReservaAsync(EventoCancelarReserva evento, CancellationToken ct)
    {
        for (var tentativa = 1; ; tentativa++)
        {
            if (await store.EventoProcessadoAsync(evento.EventId, ct)) return ResultadoCancelamentoReserva.Duplicada;
            var agora = relogio.GetUtcNow();
            await store.BloquearOrdemAsync(evento.OrdemServicoId, ct);
            var pendentes = (await store.ListarReservasAsync(evento.OrdemServicoId, ct)).Where(r => r.Status == StatusReserva.Reservada).ToList();
            store.Adicionar(new EventoProcessado { EventId = evento.EventId, ProcessadoEm = agora });
            foreach (var reserva in pendentes) reserva.Cancelar(agora);
            await store.DevolverSaldoAsync(pendentes, ct);
            store.Adicionar(Resposta(TiposEvento.InsumosReservaCancelada, evento.CorrelacaoId, evento.OrdemServicoId, ("itens", Itens(pendentes))));
            try { await store.SalvarAsync(ct); }
            catch (ConcorrenciaException) when (tentativa < TentativasMaximas) { store.DescartarAlteracoes(); continue; }
            return ResultadoCancelamentoReserva.Cancelada;
        }
    }

    public Task<IReadOnlyList<ServicoCatalogo>> ListarServicosAsync(string? nome, CancellationToken ct) => store.ListarServicosAsync(nome, ct);
    public Task<IReadOnlyList<Insumo>> ListarInsumosAsync(string? nome, CancellationToken ct) => store.ListarInsumosAsync(nome, ct);

    public async Task<ServicoCatalogo> CriarServicoAsync(string nome, string? descricao, decimal precoUnitario, CancellationToken ct)
    {
        var servico = new ServicoCatalogo(nome, descricao, precoUnitario, relogio.GetUtcNow());
        store.Adicionar(servico);
        await store.SalvarAsync(ct);
        return servico;
    }

    public async Task<Insumo> CriarInsumoAsync(string nome, string? descricao, decimal precoUnitario, int quantidadeInicial, CancellationToken ct)
    {
        var insumo = new Insumo(nome, descricao, precoUnitario, quantidadeInicial, relogio.GetUtcNow());
        store.Adicionar(insumo);
        await store.SalvarAsync(ct);
        return insumo;
    }

    // Entrada de estoque: soma ao saldo disponível e registra a movimentação na mesma transação.
    public async Task<Insumo> RegistrarEntradaAsync(Guid insumoId, int quantidade, string responsavel, CancellationToken ct)
    {
        var movimentacao = new MovimentacaoInsumo(insumoId, quantidade, responsavel, relogio.GetUtcNow());
        if (!await store.AdicionarSaldoAsync(insumoId, quantidade, ct))
        {
            // Sem linha atualizada: o insumo não existe (404) ou o saldo passaria do teto (422).
            _ = await store.ObterInsumoAsync(insumoId, ct) ?? throw new KeyNotFoundException();
            throw new ArgumentException("Saldo máximo excedido.", nameof(quantidade));
        }
        store.Adicionar(movimentacao);
        await store.SalvarAsync(ct);
        return await store.ObterInsumoAsync(insumoId, ct) ?? throw new KeyNotFoundException();
    }

    private static object[] Itens(IEnumerable<ReservaInsumo> reservas) => reservas.Select(r => (object)new { insumoId = r.InsumoId, quantidade = r.Quantidade }).ToArray();

    // Resposta ao OS Service no envelope comum da Saga, com campos extras por tipo.
    private MensagemSaida Resposta(string tipo, Guid? correlacaoId, Guid ordemServicoId, params (string Nome, object Valor)[] extras)
    {
        var eventId = Guid.NewGuid();
        var corpo = new Dictionary<string, object?>
        {
            ["eventId"] = eventId,
            ["tipo"] = tipo,
            ["correlacaoId"] = correlacaoId,
            ["ordemServicoId"] = ordemServicoId,
            ["ocorreuEm"] = relogio.GetUtcNow()
        };
        foreach (var (nome, valor) in extras) corpo[nome] = valor;
        return new MensagemSaida
        {
            Id = Guid.NewGuid(),
            EventId = eventId,
            Destino = DestinosEvento.OsService,
            Tipo = tipo,
            Conteudo = JsonSerializer.Serialize(corpo),
            CriadoEm = relogio.GetUtcNow()
        };
    }
}
