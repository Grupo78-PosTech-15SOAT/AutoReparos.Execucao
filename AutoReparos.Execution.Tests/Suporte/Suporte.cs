using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Amazon;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using AutoReparos.Execution.Application;
using AutoReparos.Execution.Domain;
using AutoReparos.Execution.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;

namespace AutoReparos.Execution.Tests.Suporte;

// Um contêiner PostgreSQL por execução; cada teste recebe um banco novo e migrado.
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").WithCommand("-c", "max_connections=500").Build();
    public Task InitializeAsync() => _postgres.StartAsync();
    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    public async Task<string> CriarBancoAsync()
    {
        var nome = "exec_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(_postgres.GetConnectionString()))
        {
            await admin.OpenAsync();
            await using var comando = new NpgsqlCommand($"CREATE DATABASE {nome}", admin);
            await comando.ExecuteNonQueryAsync();
        }
        var conexao = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = nome }.ConnectionString;
        await using var db = new ExecucaoDbContext(new DbContextOptionsBuilder<ExecucaoDbContext>().UseNpgsql(conexao).Options);
        await db.Database.MigrateAsync();
        return conexao;
    }
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;

public sealed class AlertasFalsos : IAlertSink
{
    private readonly List<(string Codigo, string Detalhe)> _itens = [];
    public IReadOnlyList<(string Codigo, string Detalhe)> Itens { get { lock (_itens) return [.. _itens]; } }
    public Task AlertarAsync(string codigo, string detalhe, CancellationToken ct) { lock (_itens) _itens.Add((codigo, detalhe)); return Task.CompletedTask; }
}

public sealed class RelogioFixo(DateTimeOffset agora) : TimeProvider
{
    public DateTimeOffset Agora { get; set; } = agora;
    public override DateTimeOffset GetUtcNow() => Agora;
}

// Registra esperas sem dormir e permite fixar o jitter.
public sealed class EsperasRegistradas
{
    private readonly List<TimeSpan> _esperas = [];
    public IReadOnlyList<TimeSpan> Itens { get { lock (_esperas) return [.. _esperas]; } }
    public Task Aguardar(TimeSpan espera, CancellationToken ct) { lock (_esperas) _esperas.Add(espera); return Task.CompletedTask; }
    public PoliticaRetentativa Politica(int maxTentativas = 5, Func<int>? jitter = null) => new(maxTentativas, Aguardar, jitter);
}

public sealed class SqsFalso() : AmazonSQSClient(new AnonymousAWSCredentials(), new AmazonSQSConfig { RegionEndpoint = RegionEndpoint.USEast1 })
{
    private readonly object _trava = new();
    private readonly List<(string Fila, string Corpo)> _enviadas = [];
    private readonly List<(string Fila, string Recibo)> _removidas = [];
    private readonly Dictionary<string, int> _tentativas = [];
    public Queue<Message> ParaReceber { get; } = new();
    // (fila, número da tentativa naquela fila) -> exceção a lançar, ou null para aceitar.
    public Func<string, int, Exception?>? FalhaEnvio { get; set; }
    public IReadOnlyList<(string Fila, string Corpo)> Enviadas { get { lock (_trava) return [.. _enviadas]; } }
    public IReadOnlyList<(string Fila, string Recibo)> Removidas { get { lock (_trava) return [.. _removidas]; } }
    public int Tentativas(string fila) { lock (_trava) return _tentativas.GetValueOrDefault(fila); }

    public override Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, CancellationToken cancellationToken = default)
    {
        lock (_trava)
        {
            var tentativa = _tentativas[request.QueueUrl] = _tentativas.GetValueOrDefault(request.QueueUrl) + 1;
            var falha = FalhaEnvio?.Invoke(request.QueueUrl, tentativa);
            if (falha is not null) return Task.FromException<SendMessageResponse>(falha);
            _enviadas.Add((request.QueueUrl, request.MessageBody));
            return Task.FromResult(new SendMessageResponse { MessageId = Guid.NewGuid().ToString() });
        }
    }

    public override Task<DeleteMessageResponse> DeleteMessageAsync(DeleteMessageRequest request, CancellationToken cancellationToken = default)
    {
        lock (_trava) _removidas.Add((request.QueueUrl, request.ReceiptHandle));
        return Task.FromResult(new DeleteMessageResponse());
    }

    public override async Task<ReceiveMessageResponse> ReceiveMessageAsync(ReceiveMessageRequest request, CancellationToken cancellationToken = default)
    {
        var lote = new List<Message>();
        lock (_trava) while (ParaReceber.Count > 0 && lote.Count < 10) lote.Add(ParaReceber.Dequeue());
        // Simula o long polling para que o consumidor em segundo plano não gire em falso.
        if (lote.Count == 0) await Task.Delay(50, cancellationToken);
        return new ReceiveMessageResponse { Messages = lote };
    }
}

// Faz duas operações concorrentes chegarem ao SaveChanges antes de qualquer uma gravar.
public sealed class SaveGate : SaveChangesInterceptor
{
    private TaskCompletionSource _liberar = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _esperados, _chegaram;
    public void Armar(int esperados) { _liberar = new(TaskCreationOptions.RunContinuationsAsynchronously); _chegaram = 0; _esperados = esperados; }
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        var esperados = _esperados;
        if (esperados > 0 && Interlocked.Increment(ref _chegaram) <= esperados)
        {
            var liberar = _liberar;
            if (_chegaram == esperados) liberar.TrySetResult();
            await Task.WhenAny(liberar.Task, Task.Delay(TimeSpan.FromSeconds(20), cancellationToken));
        }
        return result;
    }
}

public sealed class AutenticacaoDeTeste(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var papel = Request.Headers["X-Test-Role"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(papel)) return Task.FromResult(AuthenticateResult.NoResult());
        var claims = new List<Claim> { new(ClaimTypes.Role, papel) };
        if (!Request.Headers.ContainsKey("X-Test-NoSub"))
        {
            var sub = Request.Headers["X-Test-Sub"].FirstOrDefault() ?? "mecanico-1";
            claims.Add(new Claim("sub", sub));
        }
        var identidade = new ClaimsIdentity(claims, Scheme.Name, ClaimTypes.Name, ClaimTypes.Role);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identidade), Scheme.Name)));
    }
}

public sealed class FabricaApi(string conexao, SqsFalso sqs, AlertasFalsos alertas, RelogioFixo? relogio = null, SaveGate? gate = null,
    PoliticaRetentativa? politica = null, bool segundoPlano = false, Action<IServiceCollection>? ajustar = null) : WebApplicationFactory<Program>
{
    public const string FilaLiberadas = "fila-liberadas", DlqLiberadas = "dlq-liberadas", FilaOs = "fila-os", FilaBilling = "fila-billing", DlqSaida = "dlq-saida";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Telemetry:OtlpEndpoint"] = "http://127.0.0.1:1",
            ["BackgroundServices:Enabled"] = segundoPlano ? "true" : "false",
            ["Sqs:LiberadasQueueUrl"] = FilaLiberadas,
            ["Sqs:LiberadasDeadLetterQueueUrl"] = DlqLiberadas,
            ["Sqs:OsServiceQueueUrl"] = FilaOs,
            ["Sqs:BillingQueueUrl"] = FilaBilling,
            ["Sqs:DeadLetterQueueUrl"] = DlqSaida,
        }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<ExecucaoDbContext>>();
            services.RemoveAll(typeof(Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<ExecucaoDbContext>));
            services.AddDbContext<ExecucaoDbContext>(o => { o.UseNpgsql(conexao); if (gate is not null) o.AddInterceptors(gate); });
            services.RemoveAll<Amazon.SQS.IAmazonSQS>(); services.AddSingleton<Amazon.SQS.IAmazonSQS>(sqs);
            services.RemoveAll<IAlertSink>(); services.AddSingleton<IAlertSink>(alertas);
            if (relogio is not null) { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(relogio); }
            if (politica is not null) { services.RemoveAll<PoliticaRetentativa>(); services.AddSingleton(politica); }
            services.AddAuthentication(options => { options.DefaultAuthenticateScheme = "Test"; options.DefaultChallengeScheme = "Test"; options.DefaultScheme = "Test"; })
                .AddScheme<AuthenticationSchemeOptions, AutenticacaoDeTeste>("Test", _ => { });
            ajustar?.Invoke(services);
        });
    }

    public HttpClient Cliente(string? papel = "Mecanico", string? sub = null, bool semSub = false)
    {
        var cliente = CreateClient();
        if (papel is not null) cliente.DefaultRequestHeaders.Add("X-Test-Role", papel);
        if (sub is not null) cliente.DefaultRequestHeaders.Add("X-Test-Sub", sub);
        if (semSub) cliente.DefaultRequestHeaders.Add("X-Test-NoSub", "1");
        return cliente;
    }

    public async Task<T> NoBancoAsync<T>(Func<ExecucaoDbContext, Task<T>> acao)
    {
        using var scope = Services.CreateScope();
        return await acao(scope.ServiceProvider.GetRequiredService<ExecucaoDbContext>());
    }

    public async Task<OrdemExecucao> SemearAsync(Action<OrdemExecucao>? preparar = null)
    {
        var ordem = new OrdemExecucao(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
        preparar?.Invoke(ordem);
        await NoBancoAsync(async db => { db.Ordens.Add(ordem); await db.SaveChangesAsync(); return 0; });
        return ordem;
    }
}

public static class Mensagens
{
    public const string TipoLiberada = "ordem-servico.liberada.v1";

    public static Amazon.SQS.Model.Message Liberada(Guid eventId, Guid ordemServicoId, Guid? clienteId = null, Guid? veiculoId = null, string tipo = TipoLiberada, Guid? correlacaoId = null) => new()
    {
        MessageId = "msg-" + eventId,
        ReceiptHandle = "recibo-" + eventId,
        Body = JsonSerializer.Serialize(new { eventId, tipo, correlacaoId, id = ordemServicoId, clienteId = clienteId ?? Guid.NewGuid(), veiculoId = veiculoId ?? Guid.NewGuid() })
    };

    public const string TipoCancelar = "execucao.cancelar.v1";

    public static Amazon.SQS.Model.Message Cancelar(Guid eventId, Guid ordemServicoId, string motivo = "Orçamento cancelado pelo cliente", Guid? correlacaoId = null) => new()
    {
        MessageId = "msg-" + eventId,
        ReceiptHandle = "recibo-" + eventId,
        Body = JsonSerializer.Serialize(new { eventId, tipo = TipoCancelar, correlacaoId, ordemServicoId, ocorreuEm = "2026-11-13T12:00:00Z", motivo })
    };

    public static StringContent Json(string corpo) => new(corpo, System.Text.Encoding.UTF8, "application/json");
}

public sealed class Ambiente : IDisposable
{
    public required FabricaApi Fabrica { get; init; }
    public required SqsFalso Sqs { get; init; }
    public required AlertasFalsos Alertas { get; init; }
    public required RelogioFixo Relogio { get; init; }
    public required SaveGate Gate { get; init; }
    public required EsperasRegistradas Esperas { get; init; }
    public ConsumidorOsLiberada Consumidor => Fabrica.Services.GetRequiredService<ConsumidorOsLiberada>();
    public DespachanteOutbox Despachante => Fabrica.Services.GetRequiredService<DespachanteOutbox>();

    // Por padrão as esperas de retentativa são registradas, não dormidas.
    public static async Task<Ambiente> CriarAsync(PostgresFixture postgres, bool segundoPlano = false, Action<IServiceCollection>? ajustar = null, string? conexao = null)
    {
        var sqs = new SqsFalso(); var alertas = new AlertasFalsos(); var gate = new SaveGate(); var esperas = new EsperasRegistradas();
        var relogio = new RelogioFixo(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
        conexao ??= await postgres.CriarBancoAsync();
        return new Ambiente
        {
            Fabrica = new FabricaApi(conexao, sqs, alertas, relogio, gate, esperas.Politica(), segundoPlano, ajustar),
            Sqs = sqs,
            Alertas = alertas,
            Relogio = relogio,
            Gate = gate,
            Esperas = esperas
        };
    }

    public void Dispose() { Fabrica.Dispose(); NpgsqlConnection.ClearAllPools(); }
}
