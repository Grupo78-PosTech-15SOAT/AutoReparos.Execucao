using Microsoft.EntityFrameworkCore;

namespace AutoReparos.Execucao.Api;

/// <summary>
/// Modo do Job de migration do chart (D-47, porta 7): com <c>Database:RunMigrations</c> ligado o processo só aplica as
/// migrations pendentes e encerra, sem abrir HTTP.
/// </summary>
public static class MigracaoBancoDados
{
    public const string MigrarESair = "Database:RunMigrations";

    /// <summary>Aplica as migrations e devolve o código de saída do processo: 0 com sucesso, 1 se alguma falhar.</summary>
    public static async Task<int> MigrarESairAsync<TContext>(WebApplication app) where TContext : DbContext
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(MigracaoBancoDados));
        try
        {
            await using var scope = app.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<TContext>().Database.MigrateAsync();
            logger.LogInformation("Migrations aplicadas; encerrando sem abrir HTTP.");
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Falha ao aplicar as migrations.");
            return 1;
        }
        finally
        {
            await app.DisposeAsync();
        }
    }
}
