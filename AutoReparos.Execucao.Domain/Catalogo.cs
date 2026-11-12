namespace AutoReparos.Execucao.Domain;

public static class LimitesCatalogo
{
    public const int NomeMaximo = 100;
    public const int DescricaoMaxima = 500;
    public const decimal PrecoMaximo = 1_000_000_000m;
    public const int QuantidadeMaxima = 1_000_000;
    public const int SaldoMaximo = 1_000_000_000;
    public const int ListagemMaxima = 500;
}

internal static class RegrasCatalogo
{
    public static string? DescricaoOpcional(string? descricao) =>
        descricao is null ? null : Texto.Validar(descricao, LimitesCatalogo.DescricaoMaxima, nameof(descricao));

    // Preço positivo, com até duas casas decimais e dentro do teto.
    public static decimal PrecoValido(decimal preco, string campo) =>
        preco > 0 && preco <= LimitesCatalogo.PrecoMaximo && decimal.Round(preco, 2) == preco ? preco : throw new ArgumentException($"Campo '{campo}' inválido.", campo);

    public static int QuantidadePositiva(int quantidade, string campo) =>
        quantidade is > 0 and <= LimitesCatalogo.QuantidadeMaxima ? quantidade : throw new ArgumentException($"Campo '{campo}' inválido.", campo);
}

// Serviço do catálogo: o preço unitário é lido pela Cobrança ao montar o orçamento.
public sealed class ServicoCatalogo
{
    private ServicoCatalogo() { }
    public ServicoCatalogo(string nome, string? descricao, decimal precoUnitario, DateTimeOffset criadoEm)
    {
        Id = Guid.NewGuid();
        Nome = Texto.Validar(nome, LimitesCatalogo.NomeMaximo, nameof(nome));
        Descricao = RegrasCatalogo.DescricaoOpcional(descricao);
        PrecoUnitario = RegrasCatalogo.PrecoValido(precoUnitario, nameof(precoUnitario));
        CriadoEm = criadoEm;
    }
    public Guid Id { get; private set; }
    public string Nome { get; private set; } = string.Empty;
    public string? Descricao { get; private set; }
    public decimal PrecoUnitario { get; private set; }
    public DateTimeOffset CriadoEm { get; private set; }
}

// Insumo com saldo em duas partes: `QuantidadeDisponivel` (livre para reservar) e `QuantidadeReservada` (reservado para OS em andamento).
// Os saldos só mudam por UPDATE atômico no banco (reserva, devolução, baixa e entrada); a entidade tem o saldo inicial.
public sealed class Insumo
{
    private Insumo() { }
    public Insumo(string nome, string? descricao, decimal precoUnitario, int quantidadeInicial, DateTimeOffset criadoEm)
    {
        if (quantidadeInicial is < 0 or > LimitesCatalogo.QuantidadeMaxima) throw new ArgumentException("Campo 'quantidadeInicial' inválido.", nameof(quantidadeInicial));
        Id = Guid.NewGuid();
        Nome = Texto.Validar(nome, LimitesCatalogo.NomeMaximo, nameof(nome));
        Descricao = RegrasCatalogo.DescricaoOpcional(descricao);
        PrecoUnitario = RegrasCatalogo.PrecoValido(precoUnitario, nameof(precoUnitario));
        QuantidadeDisponivel = quantidadeInicial; CriadoEm = criadoEm;
    }
    public Guid Id { get; private set; }
    public string Nome { get; private set; } = string.Empty;
    public string? Descricao { get; private set; }
    public decimal PrecoUnitario { get; private set; }
    public int QuantidadeDisponivel { get; private set; }
    public int QuantidadeReservada { get; private set; }
    public DateTimeOffset CriadoEm { get; private set; }
}

// Registro de cada entrada de estoque (quantidade positiva, responsável e momento).
public sealed class MovimentacaoInsumo
{
    private MovimentacaoInsumo() { }
    public MovimentacaoInsumo(Guid insumoId, int quantidade, string responsavel, DateTimeOffset registradaEm)
    {
        if (insumoId == Guid.Empty) throw new ArgumentException("Insumo inválido.", nameof(insumoId));
        Id = Guid.NewGuid(); InsumoId = insumoId;
        Quantidade = RegrasCatalogo.QuantidadePositiva(quantidade, nameof(quantidade));
        Responsavel = Texto.Validar(responsavel, LimitesExecucao.IdentidadeMaxima, nameof(responsavel));
        RegistradaEm = registradaEm;
    }
    public Guid Id { get; private set; }
    public Guid InsumoId { get; private set; }
    public int Quantidade { get; private set; }
    public string Responsavel { get; private set; } = string.Empty;
    public DateTimeOffset RegistradaEm { get; private set; }
}

public enum StatusReserva { Reservada = 0, Confirmada = 1, Cancelada = 2 }

// Reserva em duas fases, única por (OS, insumo): `Reservada` na aprovação, `Confirmada` na conclusão da execução ou `Cancelada` na compensação.
public sealed class ReservaInsumo
{
    private ReservaInsumo() { }
    public ReservaInsumo(Guid ordemServicoId, Guid insumoId, int quantidade, DateTimeOffset criadaEm)
    {
        if (ordemServicoId == Guid.Empty || insumoId == Guid.Empty) throw new ArgumentException("Reserva inválida.");
        Id = Guid.NewGuid(); OrdemServicoId = ordemServicoId; InsumoId = insumoId;
        Quantidade = RegrasCatalogo.QuantidadePositiva(quantidade, nameof(quantidade));
        Status = StatusReserva.Reservada; CriadaEm = criadaEm; AtualizadaEm = criadaEm;
    }
    public Guid Id { get; private set; }
    public Guid OrdemServicoId { get; private set; }
    public Guid InsumoId { get; private set; }
    public int Quantidade { get; private set; }
    public StatusReserva Status { get; private set; }
    public DateTimeOffset CriadaEm { get; private set; }
    public DateTimeOffset AtualizadaEm { get; private set; }

    public void Confirmar(DateTimeOffset em)
    {
        if (Status != StatusReserva.Reservada) throw new TransicaoInvalidaException($"Reserva em estado {Status} não pode ser confirmada.");
        Status = StatusReserva.Confirmada; AtualizadaEm = em;
    }

    public void Cancelar(DateTimeOffset em)
    {
        if (Status != StatusReserva.Reservada) throw new TransicaoInvalidaException($"Reserva em estado {Status} não pode ser cancelada.");
        Status = StatusReserva.Cancelada; AtualizadaEm = em;
    }
}
