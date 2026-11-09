using System.Text;

namespace AutoReparos.Execution.Domain;

public enum EstadoExecucao { Fila = 0, EmExecucao = 1, Concluida = 2, Falhou = 3 }
public enum TipoEtapa { Diagnostico = 0, Reparo = 1 }

public static class LimitesExecucao
{
    public const int TextoMaximo = 2000;
    public const int IdentidadeMaxima = 200;
}

public sealed class TransicaoInvalidaException(string mensagem) : InvalidOperationException(mensagem);

internal static class Texto
{
    private static readonly UTF8Encoding Estrito = new(false, true);

    // Rejeita vazio, acima do limite, NUL (o PostgreSQL recusa em text) e surrogate isolado.
    public static string Validar(string? valor, int maximo, string campo)
    {
        if (string.IsNullOrWhiteSpace(valor) || valor.Length > maximo || valor.Contains((char)0))
            throw new ArgumentException($"Campo '{campo}' inválido.", campo);
        try { Estrito.GetByteCount(valor); }
        catch (EncoderFallbackException) { throw new ArgumentException($"Campo '{campo}' inválido.", campo); }
        return valor.Trim();
    }
}
