using System.Net;
using AutoReparos.Execucao.Infrastructure;
using AutoReparos.Execucao.Tests.Suporte;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;

namespace AutoReparos.Execucao.Tests;

/// <summary>
/// C9: no cluster o chart liga <c>Database__SkipStartupMigration=true</c> e quem migra é o Job; sem a flag (compose) o
/// startup continua migrando. A fábrica roda fora do ambiente <c>Testing</c>, que já não migra no startup.
/// </summary>
[Collection(AmbienteDoProcesso.Nome)]
public sealed class MigracaoNaInicializacaoTests
{
    [Fact]
    public async Task ComSkipStartupMigrationOStartupNaoCriaTabelas()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await postgres.StartAsync();

        var saude = await AmbienteDoProcesso.ComVariaveisAsync(
            new Dictionary<string, string> { ["Database__SkipStartupMigration"] = "true" },
            () => SubirAsync(postgres.GetConnectionString()));

        Assert.Equal(HttpStatusCode.OK, saude);
        Assert.Empty(await TabelasAsync(postgres.GetConnectionString()));
    }

    [Fact]
    public async Task SemAFlagOStartupContinuaMigrando()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await postgres.StartAsync();

        var saude = await SubirAsync(postgres.GetConnectionString());

        Assert.Equal(HttpStatusCode.OK, saude);
        await using var db = Contexto(postgres.GetConnectionString());
        var esperadas = db.Database.GetMigrations().ToList();
        Assert.NotEmpty(esperadas);
        Assert.Equal(esperadas, await db.Database.GetAppliedMigrationsAsync());
        Assert.Contains("ordens_execucao", await TabelasAsync(postgres.GetConnectionString()));
    }

    /// <summary>Sobe o app inteiro (o startup roda até o <c>app.Run</c>) e devolve o status do /health.</summary>
    private static async Task<HttpStatusCode> SubirAsync(string conexao)
    {
        await using var fabrica = new Fabrica(conexao);
        using var cliente = fabrica.CreateClient();
        return (await cliente.GetAsync("/health")).StatusCode;
    }

    private static async Task<List<string>> TabelasAsync(string conexao)
    {
        await using var db = Contexto(conexao);
        return await db.Database
            .SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public'")
            .ToListAsync();
    }

    private static ExecucaoDbContext Contexto(string conexao) =>
        new(new DbContextOptionsBuilder<ExecucaoDbContext>().UseNpgsql(conexao).Options);

    private sealed class Fabrica(string conexao) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<ExecucaoDbContext>>();
                services.RemoveAll(typeof(Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<ExecucaoDbContext>));
                services.AddDbContext<ExecucaoDbContext>(o => o.UseNpgsql(conexao));
                // Os consumidores de fila e o outbox do serviço não fazem parte do que se prova aqui.
                foreach (var hospedado in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType?.Assembly == typeof(Program).Assembly).ToList())
                    services.Remove(hospedado);
            });
        }
    }
}
