using System.Text.Json;
using AutoReparos.Execucao.Domain;

namespace AutoReparos.Execucao.Application;

public static class TiposEvento
{
    public const string OsLiberada = "ordem-servico.liberada.v1";
    public const string ExecucaoConcluida = "execucao.concluida.v1";
    public const string ExecucaoFalhou = "execucao.falhou.v1";
    public const string ExecucaoCancelar = "execucao.cancelar.v1";
    public const string ExecucaoCancelada = "execucao.cancelada.v1";
    public const string ExecucaoCancelamentoRecusado = "execucao.cancelamento-recusado.v1";
    public const string ExecucaoLiberacaoRecusada = "execucao.liberacao-recusada.v1";
}

public static class DestinosEvento
{
    public const string OsService = "os-service";
    public const string Billing = "billing";
}

public enum ResultadoRecebimento { Enfileirada, Duplicada, Recusada }
public enum ResultadoCancelamento { Cancelada, Recusada, Duplicada }

public sealed record EventoOsLiberada(Guid EventId, Guid OrdemServicoId, Guid ClienteId, Guid VeiculoId, Guid? CorrelacaoId = null)
{
    // Contrato do OS Service (camelCase): eventId, tipo, id, clienteId, veiculoId e, opcional, correlacaoId. GUIDs só no formato canônico 8-4-4-4-12.
    public static bool TentarLer(string? json, out EventoOsLiberada? evento)
    {
        evento = null;
        if (!LeitorJson.TentarLerObjeto(json, out var corpo)) return false;
        if (!LeitorJson.TentarLerTexto(corpo, "tipo", 100, out var tipo) || tipo != TiposEvento.OsLiberada) return false;
        if (!LeitorJson.TentarLerGuid(corpo, "eventId", out var eventId) || eventId == Guid.Empty
            || !LeitorJson.TentarLerGuid(corpo, "id", out var ordemId) || ordemId == Guid.Empty
            || !LeitorJson.TentarLerGuid(corpo, "clienteId", out var clienteId) || clienteId == Guid.Empty
            || !LeitorJson.TentarLerGuid(corpo, "veiculoId", out var veiculoId) || veiculoId == Guid.Empty
            || !LeitorJson.TentarLerGuidOpcional(corpo, "correlacaoId", out var correlacaoId)) return false;
        evento = new EventoOsLiberada(eventId, ordemId, clienteId, veiculoId, correlacaoId);
        return true;
    }
}

public sealed record EventoCancelarExecucao(Guid EventId, Guid OrdemServicoId, string Motivo, Guid? CorrelacaoId)
{
    // Comando do OS Service (camelCase): eventId, tipo, ordemServicoId, motivo e, opcional, correlacaoId.
    public static bool TentarLer(string? json, out EventoCancelarExecucao? evento)
    {
        evento = null;
        if (!LeitorJson.TentarLerObjeto(json, out var corpo)) return false;
        if (!LeitorJson.TentarLerTexto(corpo, "tipo", 100, out var tipo) || tipo != TiposEvento.ExecucaoCancelar) return false;
        if (!LeitorJson.TentarLerGuid(corpo, "eventId", out var eventId) || eventId == Guid.Empty
            || !LeitorJson.TentarLerGuid(corpo, "ordemServicoId", out var ordemId) || ordemId == Guid.Empty
            || !LeitorJson.TentarLerTexto(corpo, "motivo", LimitesExecucao.TextoMaximo, out var motivo)
            || !LeitorJson.TentarLerGuidOpcional(corpo, "correlacaoId", out var correlacaoId)) return false;
        evento = new EventoCancelarExecucao(eventId, ordemId, motivo!, correlacaoId);
        return true;
    }
}

public sealed class ConcorrenciaException(string mensagem, Exception? inner = null) : Exception(mensagem, inner);
public sealed class DependenciaIndisponivelException(string dependencia, Exception? inner = null) : Exception($"Dependência indisponível: {dependencia}.", inner)
{
    public string Dependencia { get; } = dependencia;
}

public interface IExecucaoStore
{
    Task<OrdemExecucao?> ObterAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<OrdemExecucao>> ListarFilaAsync(IReadOnlyCollection<EstadoExecucao> estados, string? mecanico, CancellationToken ct);
    Task<bool> EventoProcessadoAsync(Guid eventId, CancellationToken ct);
    Task<OrdemExecucao?> ObterPorOrdemServicoAsync(Guid ordemServicoId, CancellationToken ct);
    void Adicionar(OrdemExecucao ordem);
    void Adicionar(EventoProcessado evento);
    void Adicionar(MensagemSaida mensagem);
    void DescartarAlteracoes();
    Task SalvarAsync(CancellationToken ct);
}

public interface IAlertSink { Task AlertarAsync(string codigo, string detalhe, CancellationToken ct); }

public sealed class ExecucaoService(IExecucaoStore store, TimeProvider relogio)
{
    public async Task<ResultadoRecebimento> ReceberOsLiberadaAsync(EventoOsLiberada evento, CancellationToken ct)
    {
        if (await store.EventoProcessadoAsync(evento.EventId, ct)) return ResultadoRecebimento.Duplicada;
        var agora = relogio.GetUtcNow();
        var existente = await store.ObterPorOrdemServicoAsync(evento.OrdemServicoId, ct);
        store.Adicionar(new EventoProcessado { EventId = evento.EventId, ProcessadoEm = agora });
        // Regra de negócio: OS cuja execução já terminou (cancelada ou concluída) não volta à fila; o OS Service é avisado.
        var motivoRecusa = existente?.Estado switch
        {
            EstadoExecucao.Cancelada => "Execução da OS já foi cancelada.",
            EstadoExecucao.Concluida => "Execução da OS já foi concluída.",
            _ => null
        };
        if (motivoRecusa is not null)
            store.Adicionar(Resposta(TiposEvento.ExecucaoLiberacaoRecusada, evento.CorrelacaoId, evento.OrdemServicoId, null, motivoRecusa));
        else if (existente is null) store.Adicionar(new OrdemExecucao(evento.OrdemServicoId, evento.ClienteId, evento.VeiculoId, agora, evento.CorrelacaoId));
        try { await store.SalvarAsync(ct); }
        catch (ConcorrenciaException)
        {
            // Entrega concorrente do mesmo evento (ou da mesma OS): a outra transação venceu; nada a duplicar.
            store.DescartarAlteracoes();
            return ResultadoRecebimento.Duplicada;
        }
        return motivoRecusa is not null ? ResultadoRecebimento.Recusada : existente is not null ? ResultadoRecebimento.Duplicada : ResultadoRecebimento.Enfileirada;
    }

    // Compensação da Saga. Refaz a leitura quando outra operação altera a execução ao mesmo tempo; a entrega repetida é coberta pela deduplicação por eventId.
    public async Task<ResultadoCancelamento> CancelarPorOsAsync(EventoCancelarExecucao evento, CancellationToken ct)
    {
        for (var tentativa = 1; ; tentativa++)
        {
            if (await store.EventoProcessadoAsync(evento.EventId, ct)) return ResultadoCancelamento.Duplicada;
            var agora = relogio.GetUtcNow();
            var ordem = await store.ObterPorOrdemServicoAsync(evento.OrdemServicoId, ct);
            store.Adicionar(new EventoProcessado { EventId = evento.EventId, ProcessadoEm = agora });
            string? recusa = ordem?.Estado switch
            {
                null => "Execução da OS não encontrada.",
                EstadoExecucao.Concluida => "Execução já concluída não pode ser cancelada.",
                _ => null
            };
            // Cancelamento repetido com outro eventId confirma de novo, sem alterar a execução.
            if (recusa is null && ordem!.Estado != EstadoExecucao.Cancelada) ordem.Cancelar(evento.Motivo, agora);
            var tipo = recusa is null ? TiposEvento.ExecucaoCancelada : TiposEvento.ExecucaoCancelamentoRecusado;
            store.Adicionar(Resposta(tipo, evento.CorrelacaoId ?? ordem?.CorrelacaoId, evento.OrdemServicoId, ordem?.Id, recusa));
            try { await store.SalvarAsync(ct); }
            catch (ConcorrenciaException) when (tentativa < 3) { store.DescartarAlteracoes(); continue; }
            return recusa is null ? ResultadoCancelamento.Cancelada : ResultadoCancelamento.Recusada;
        }
    }

    public async Task<IReadOnlyList<OrdemExecucao>> ListarFilaAsync(IReadOnlyCollection<EstadoExecucao>? estados, string? mecanico, CancellationToken ct) =>
        await store.ListarFilaAsync(estados is { Count: > 0 } ? estados : [EstadoExecucao.Fila, EstadoExecucao.EmExecucao, EstadoExecucao.Falhou], mecanico, ct);

    public async Task<EtapaExecucao> RegistrarEtapaAsync(Guid id, TipoEtapa tipo, string descricao, string responsavel, CancellationToken ct)
    {
        var ordem = await store.ObterAsync(id, ct) ?? throw new KeyNotFoundException();
        var etapa = ordem.RegistrarEtapa(tipo, descricao, responsavel, relogio.GetUtcNow());
        await store.SalvarAsync(ct);
        return etapa;
    }

    public async Task<OrdemExecucao> ConcluirAsync(Guid id, CancellationToken ct)
    {
        var ordem = await store.ObterAsync(id, ct) ?? throw new KeyNotFoundException();
        ordem.Concluir(relogio.GetUtcNow());
        // Mesmo eventId para os dois consumidores; cada destino tem sua própria linha no outbox.
        var eventId = Guid.NewGuid();
        var conteudo = JsonSerializer.Serialize(new
        {
            eventId,
            tipo = TiposEvento.ExecucaoConcluida,
            correlacaoId = ordem.CorrelacaoId,
            ocorreuEm = relogio.GetUtcNow(),
            id = ordem.Id,
            ordemServicoId = ordem.OrdemServicoId,
            clienteId = ordem.ClienteId,
            veiculoId = ordem.VeiculoId,
            concluidaEm = ordem.ConcluidaEm
        });
        foreach (var destino in new[] { DestinosEvento.OsService, DestinosEvento.Billing })
            store.Adicionar(NovaMensagem(eventId, destino, TiposEvento.ExecucaoConcluida, conteudo));
        await store.SalvarAsync(ct);
        return ordem;
    }

    public async Task<OrdemExecucao> FalharAsync(Guid id, string motivo, CancellationToken ct)
    {
        var ordem = await store.ObterAsync(id, ct) ?? throw new KeyNotFoundException();
        ordem.Falhar(motivo, relogio.GetUtcNow());
        var eventId = Guid.NewGuid();
        var conteudo = JsonSerializer.Serialize(new
        {
            eventId,
            tipo = TiposEvento.ExecucaoFalhou,
            correlacaoId = ordem.CorrelacaoId,
            ocorreuEm = relogio.GetUtcNow(),
            id = ordem.Id,
            ordemServicoId = ordem.OrdemServicoId,
            clienteId = ordem.ClienteId,
            veiculoId = ordem.VeiculoId,
            motivo = ordem.MotivoFalha,
            falhouEm = ordem.FalhouEm
        });
        store.Adicionar(NovaMensagem(eventId, DestinosEvento.OsService, TiposEvento.ExecucaoFalhou, conteudo));
        await store.SalvarAsync(ct);
        return ordem;
    }

    public async Task<OrdemExecucao> ReexecutarAsync(Guid id, string motivo, string supervisor, CancellationToken ct)
    {
        var ordem = await store.ObterAsync(id, ct) ?? throw new KeyNotFoundException();
        ordem.Reexecutar(motivo, supervisor, relogio.GetUtcNow());
        await store.SalvarAsync(ct);
        return ordem;
    }

    // Resposta para o OS Service no envelope comum da Saga; `execucaoId` só aparece quando a execução existe e `motivo` só nas recusas.
    private MensagemSaida Resposta(string tipo, Guid? correlacaoId, Guid ordemServicoId, Guid? execucaoId, string? motivo)
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
        if (execucaoId is not null) corpo["execucaoId"] = execucaoId;
        if (motivo is not null) corpo["motivo"] = motivo;
        return NovaMensagem(eventId, DestinosEvento.OsService, tipo, JsonSerializer.Serialize(corpo));
    }

    private MensagemSaida NovaMensagem(Guid eventId, string destino, string tipo, string conteudo) =>
        new() { Id = Guid.NewGuid(), EventId = eventId, Destino = destino, Tipo = tipo, Conteudo = conteudo, CriadoEm = relogio.GetUtcNow() };
}
