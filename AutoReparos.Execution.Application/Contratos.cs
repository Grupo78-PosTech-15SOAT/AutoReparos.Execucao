using System.Text.Json;
using AutoReparos.Execution.Domain;

namespace AutoReparos.Execution.Application;

public static class TiposEvento
{
    public const string OsLiberada = "ordem-servico.liberada.v1";
    public const string ExecucaoConcluida = "execucao.concluida.v1";
    public const string ExecucaoFalhou = "execucao.falhou.v1";
}

public static class DestinosEvento
{
    public const string OsService = "os-service";
    public const string Billing = "billing";
}

public enum ResultadoRecebimento { Enfileirada, Duplicada }

public sealed record EventoOsLiberada(Guid EventId, Guid OrdemServicoId, Guid ClienteId, Guid VeiculoId)
{
    // Contrato do OS Service (camelCase): eventId, tipo, id, clienteId, veiculoId. GUIDs só no formato canônico 8-4-4-4-12.
    public static bool TentarLer(string? json, out EventoOsLiberada? evento)
    {
        evento = null;
        if (!LeitorJson.TentarLerObjeto(json, out var corpo)) return false;
        if (!LeitorJson.TentarLerTexto(corpo, "tipo", 100, out var tipo) || tipo != TiposEvento.OsLiberada) return false;
        if (!LeitorJson.TentarLerGuid(corpo, "eventId", out var eventId) || eventId == Guid.Empty
            || !LeitorJson.TentarLerGuid(corpo, "id", out var ordemId) || ordemId == Guid.Empty
            || !LeitorJson.TentarLerGuid(corpo, "clienteId", out var clienteId) || clienteId == Guid.Empty
            || !LeitorJson.TentarLerGuid(corpo, "veiculoId", out var veiculoId) || veiculoId == Guid.Empty) return false;
        evento = new EventoOsLiberada(eventId, ordemId, clienteId, veiculoId);
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
    Task<bool> OrdemServicoNaFilaAsync(Guid ordemServicoId, CancellationToken ct);
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
        var jaNaFila = await store.OrdemServicoNaFilaAsync(evento.OrdemServicoId, ct);
        store.Adicionar(new EventoProcessado { EventId = evento.EventId, ProcessadoEm = agora });
        if (!jaNaFila) store.Adicionar(new OrdemExecucao(evento.OrdemServicoId, evento.ClienteId, evento.VeiculoId, agora));
        try { await store.SalvarAsync(ct); }
        catch (ConcorrenciaException)
        {
            // Entrega concorrente do mesmo evento (ou da mesma OS): a outra transação venceu; nada a duplicar.
            store.DescartarAlteracoes();
            return ResultadoRecebimento.Duplicada;
        }
        return jaNaFila ? ResultadoRecebimento.Duplicada : ResultadoRecebimento.Enfileirada;
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

    private MensagemSaida NovaMensagem(Guid eventId, string destino, string tipo, string conteudo) =>
        new() { Id = Guid.NewGuid(), EventId = eventId, Destino = destino, Tipo = tipo, Conteudo = conteudo, CriadoEm = relogio.GetUtcNow() };
}
