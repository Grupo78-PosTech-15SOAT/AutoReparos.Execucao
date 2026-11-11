namespace AutoReparos.Execucao.Domain;

public sealed class EtapaExecucao
{
    private EtapaExecucao() { }
    internal EtapaExecucao(Guid ordemExecucaoId, int sequencia, TipoEtapa tipo, string descricao, string responsavel, DateTimeOffset registradaEm)
    {
        if (!Enum.IsDefined(tipo)) throw new ArgumentException("Tipo de etapa inválido.", nameof(tipo));
        Id = Guid.NewGuid(); OrdemExecucaoId = ordemExecucaoId; Sequencia = sequencia; Tipo = tipo;
        Descricao = Texto.Validar(descricao, LimitesExecucao.TextoMaximo, nameof(descricao));
        Responsavel = Texto.Validar(responsavel, LimitesExecucao.IdentidadeMaxima, nameof(responsavel));
        RegistradaEm = registradaEm;
    }
    public Guid Id { get; private set; }
    public Guid OrdemExecucaoId { get; private set; }
    public int Sequencia { get; private set; }
    public TipoEtapa Tipo { get; private set; }
    public string Descricao { get; private set; } = string.Empty;
    public string Responsavel { get; private set; } = string.Empty;
    public DateTimeOffset RegistradaEm { get; private set; }
}
