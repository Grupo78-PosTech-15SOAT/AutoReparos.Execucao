using System.Reflection;
using System.Runtime.ExceptionServices;

namespace AutoReparos.Execucao.Tests.Suporte;

/// <summary>
/// Testes que configuram o serviço por variáveis de ambiente, como o chart faz com o contêiner. O
/// <c>WebApplication.CreateBuilder</c> lê o ambiente do processo inteiro, então esta coleção não roda em paralelo
/// com as outras.
/// </summary>
[CollectionDefinition(Nome, DisableParallelization = true)]
public sealed class AmbienteDoProcesso
{
    public const string Nome = "ambiente do processo";

    /// <summary>Define as variáveis durante a ação e restaura os valores anteriores ao final.</summary>
    public static async Task<T> ComVariaveisAsync<T>(IReadOnlyDictionary<string, string> variaveis, Func<Task<T>> acao)
    {
        var anteriores = variaveis.Keys.ToDictionary(chave => chave, Environment.GetEnvironmentVariable);
        foreach (var (chave, valor) in variaveis) Environment.SetEnvironmentVariable(chave, valor);
        try
        {
            return await acao();
        }
        finally
        {
            foreach (var (chave, valor) in anteriores) Environment.SetEnvironmentVariable(chave, valor);
        }
    }

    /// <summary>Executa o <c>Main</c> real do serviço e devolve o código de saída que o processo teria.</summary>
    public static Task<int> ExecutarMainAsync(params string[] args)
    {
        var main = typeof(Program).Assembly.EntryPoint ?? throw new InvalidOperationException("Program sem ponto de entrada.");
        return Task.Run(() =>
        {
            try
            {
                return (int)main.Invoke(null, [args])!;
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }).WaitAsync(TimeSpan.FromMinutes(2));
    }
}
