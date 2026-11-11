namespace AutoReparos.Execucao.Domain;

public sealed class ReexecucaoExecucao
{
    private ReexecucaoExecucao() { }
    internal ReexecucaoExecucao(Guid ordemExecucaoId, string motivo, string supervisor, DateTimeOffset solicitadaEm)
    {
        Id = Guid.NewGuid(); OrdemExecucaoId = ordemExecucaoId;
        Motivo = Texto.Validar(motivo, LimitesExecucao.TextoMaximo, nameof(motivo));
        Supervisor = Texto.Validar(supervisor, LimitesExecucao.IdentidadeMaxima, nameof(supervisor));
        SolicitadaEm = solicitadaEm;
    }
    public Guid Id { get; private set; }
    public Guid OrdemExecucaoId { get; private set; }
    public string Motivo { get; private set; } = string.Empty;
    public string Supervisor { get; private set; } = string.Empty;
    public DateTimeOffset SolicitadaEm { get; private set; }
}
