namespace AutoReparos.Execucao.Domain;

// Linha do outbox transacional: gravada na mesma transação da mudança de estado e publicada depois.
public sealed class MensagemSaida
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public string Destino { get; set; } = string.Empty;
    public string Tipo { get; set; } = string.Empty;
    public string Conteudo { get; set; } = string.Empty;
    public DateTimeOffset CriadoEm { get; set; }
    public DateTimeOffset? EnviadoEm { get; set; }
    public int Tentativas { get; set; }
    public DateTimeOffset? ProximaTentativaEm { get; set; }
    public bool EncaminhadoParaDlq { get; set; }
}

public sealed class EventoProcessado
{
    public Guid EventId { get; set; }
    public DateTimeOffset ProcessadoEm { get; set; }
}
