namespace AutoReparos.Execucao.Domain;

public sealed class OrdemExecucao
{
    private readonly List<EtapaExecucao> _etapas = [];
    private readonly List<ReexecucaoExecucao> _reexecucoes = [];

    private OrdemExecucao() { }
    public OrdemExecucao(Guid ordemServicoId, Guid clienteId, Guid veiculoId, DateTimeOffset criadaEm, Guid? correlacaoId = null)
    {
        if (ordemServicoId == Guid.Empty || clienteId == Guid.Empty || veiculoId == Guid.Empty || correlacaoId == Guid.Empty) throw new ArgumentException("Ordem de execução inválida.");
        Id = Guid.NewGuid(); OrdemServicoId = ordemServicoId; ClienteId = clienteId; VeiculoId = veiculoId;
        CorrelacaoId = correlacaoId; Estado = EstadoExecucao.Fila; CriadaEm = criadaEm; AtualizadaEm = criadaEm;
    }

    public Guid Id { get; private set; }
    public Guid OrdemServicoId { get; private set; }
    public Guid ClienteId { get; private set; }
    public Guid VeiculoId { get; private set; }
    // Identificador da Saga que liberou a OS; nulo quando a mensagem de liberação não o trouxe.
    public Guid? CorrelacaoId { get; private set; }
    public EstadoExecucao Estado { get; private set; }
    public bool IntervencaoRequerida { get; private set; }
    public string? MotivoFalha { get; private set; }
    public DateTimeOffset CriadaEm { get; private set; }
    public DateTimeOffset AtualizadaEm { get; private set; }
    public DateTimeOffset? ConcluidaEm { get; private set; }
    public DateTimeOffset? FalhouEm { get; private set; }
    public string? MotivoCancelamento { get; private set; }
    public DateTimeOffset? CanceladaEm { get; private set; }
    // Cada mudança altera esta coluna, então o pai muda mesmo quando só uma etapa filha é inserida e a concorrência otimista (xmin) cobre o agregado.
    public int Revisao { get; private set; }
    public IReadOnlyList<EtapaExecucao> Etapas => _etapas;
    public IReadOnlyList<ReexecucaoExecucao> Reexecucoes => _reexecucoes;

    public EtapaExecucao RegistrarEtapa(TipoEtapa tipo, string descricao, string responsavel, DateTimeOffset em)
    {
        if (Estado is not (EstadoExecucao.Fila or EstadoExecucao.EmExecucao))
            throw new TransicaoInvalidaException($"Execução em estado {Estado} não aceita novas etapas.");
        var etapa = new EtapaExecucao(Id, _etapas.Count + 1, tipo, descricao, responsavel, em);
        _etapas.Add(etapa); Estado = EstadoExecucao.EmExecucao; Tocar(em);
        return etapa;
    }

    public void Concluir(DateTimeOffset em)
    {
        if (Estado != EstadoExecucao.EmExecucao) throw new TransicaoInvalidaException($"Só uma execução em andamento pode ser concluída (estado atual: {Estado}).");
        Estado = EstadoExecucao.Concluida; ConcluidaEm = em; Tocar(em);
    }

    public void Falhar(string motivo, DateTimeOffset em)
    {
        if (Estado is not (EstadoExecucao.Fila or EstadoExecucao.EmExecucao)) throw new TransicaoInvalidaException($"Execução em estado {Estado} não pode falhar.");
        MotivoFalha = Texto.Validar(motivo, LimitesExecucao.TextoMaximo, nameof(motivo));
        Estado = EstadoExecucao.Falhou; IntervencaoRequerida = true; FalhouEm = em; Tocar(em);
    }

    // Reexecução manual: só após falha; as etapas já registradas são preservadas.
    public ReexecucaoExecucao Reexecutar(string motivo, string supervisor, DateTimeOffset em)
    {
        if (Estado != EstadoExecucao.Falhou) throw new TransicaoInvalidaException($"Só uma execução em falha pode ser reexecutada (estado atual: {Estado}).");
        var registro = new ReexecucaoExecucao(Id, motivo, supervisor, em);
        _reexecucoes.Add(registro);
        Estado = _etapas.Count > 0 ? EstadoExecucao.EmExecucao : EstadoExecucao.Fila;
        IntervencaoRequerida = false; Tocar(em);
        return registro;
    }

    // Cancelamento (compensação da Saga): em Fila, EmExecucao ou Falhou (a intervenção deixa de ser necessária); as etapas já registradas são preservadas.
    public void Cancelar(string motivo, DateTimeOffset em)
    {
        if (Estado is not (EstadoExecucao.Fila or EstadoExecucao.EmExecucao or EstadoExecucao.Falhou)) throw new TransicaoInvalidaException($"Execução em estado {Estado} não pode ser cancelada.");
        MotivoCancelamento = Texto.Validar(motivo, LimitesExecucao.TextoMaximo, nameof(motivo));
        Estado = EstadoExecucao.Cancelada; IntervencaoRequerida = false; CanceladaEm = em; Tocar(em);
    }

    private void Tocar(DateTimeOffset em) { AtualizadaEm = em; Revisao++; }
}
