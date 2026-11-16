using System.Net;
using System.Net.Sockets;
using AutoReparos.Execucao.Infrastructure;
using AutoReparos.Execucao.Tests.Suporte;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace AutoReparos.Execucao.Tests;

/// <summary>
/// C7: o Job de migration do chart roda a imagem da Execução com <c>Database__RunMigrations=true</c>; o processo aplica
/// as migrations e encerra sem abrir HTTP. A porta passada em <c>--urls</c> fica ocupada pelo teste: se o serviço
/// abrisse o Kestrel, o <c>Main</c> falharia com endereço em uso em vez de devolver o código de saída.
/// </summary>
[Collection(AmbienteDoProcesso.Nome)]
public sealed class MigrarESairTests
{
    [Fact]
    public async Task DatabaseVaziaRecebeTodasAsMigrationsESaiComZero()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await postgres.StartAsync();

        var codigo = await MigrarESairAsync(postgres.GetConnectionString());

        Assert.Equal(0, codigo);
        await using var db = Contexto(postgres.GetConnectionString());
        var esperadas = db.Database.GetMigrations().ToList();
        Assert.NotEmpty(esperadas);
        Assert.Equal(esperadas, await db.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task MigrationQueFalhaSaiComCodigoDiferenteDeZero()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await postgres.StartAsync();
        await using (var db = Contexto(postgres.GetConnectionString()))
        {
            // A primeira migration cria ordens_execucao: com a tabela já existente ela falha.
            await db.Database.ExecuteSqlRawAsync("CREATE TABLE ordens_execucao (id integer)");
        }

        var codigo = await MigrarESairAsync(postgres.GetConnectionString());

        Assert.NotEqual(0, codigo);
    }

    private static async Task<int> MigrarESairAsync(string conexao)
    {
        var ocupante = new TcpListener(IPAddress.Loopback, 0);
        ocupante.Start();
        try
        {
            var porta = ((IPEndPoint)ocupante.LocalEndpoint).Port;
            return await AmbienteDoProcesso.ComVariaveisAsync(
                new Dictionary<string, string>
                {
                    ["Database__RunMigrations"] = "true",
                    ["ConnectionStrings__Execucao"] = conexao,
                },
                () => AmbienteDoProcesso.ExecutarMainAsync($"--urls=http://127.0.0.1:{porta}"));
        }
        finally
        {
            ocupante.Stop();
        }
    }

    private static ExecucaoDbContext Contexto(string conexao) =>
        new(new DbContextOptionsBuilder<ExecucaoDbContext>().UseNpgsql(conexao).Options);
}
