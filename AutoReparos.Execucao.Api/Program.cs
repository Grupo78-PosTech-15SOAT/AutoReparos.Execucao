using System.Security.Claims;
using Amazon.SQS;
using AutoReparos.Execucao.Api;
using AutoReparos.Execucao.Application;
using AutoReparos.Execucao.Domain;
using AutoReparos.Execucao.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
var serviceName = builder.Configuration["Telemetry:ServiceName"] ?? "autoreparos-execution";
var otlpEndpoint = builder.Configuration["Telemetry:OtlpEndpoint"] ?? "http://localhost:4317";
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => TelemetriaExecucao.Configurar(resource, serviceName, builder.Environment.EnvironmentName))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter(options => options.Endpoint = new Uri(otlpEndpoint)))
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        .AddOtlpExporter(options => options.Endpoint = new Uri(otlpEndpoint)));
builder.Logging.AddOpenTelemetry(logging =>
{
    logging.IncludeFormattedMessage = true;
    logging.IncludeScopes = true;
    logging.ParseStateValues = true;
    logging.AddOtlpExporter(options => options.Endpoint = new Uri(otlpEndpoint));
});
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 64 * 1024);
builder.Services.AddOpenApi();
builder.Services.AddDbContext<ExecucaoDbContext>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Execucao")));
builder.Services.AddScoped<IExecucaoStore>(sp => sp.GetRequiredService<ExecucaoDbContext>());
builder.Services.AddScoped<ExecucaoService>();
builder.Services.AddScoped<EstoqueService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IAmazonSQS, AmazonSQSClient>();
builder.Services.AddSingleton<IAlertSink, LoggingAlertSink>();
builder.Services.AddSingleton(PoliticaRetentativa.Padrao);
builder.Services.AddSingleton<PublicadorSqsEventos>();
builder.Services.AddSingleton<DespachanteOutbox>();
builder.Services.AddSingleton<ConsumidorOsLiberada>();
builder.Services.AddHostedService<PublicadorOutboxServico>();
builder.Services.AddHostedService<ConsumidorOsLiberadaServico>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.Authority = builder.Configuration["Authentication:Authority"];
    options.Audience = builder.Configuration["Authentication:Audience"];
    options.RequireHttpsMetadata = builder.Configuration.GetValue("Authentication:RequireHttpsMetadata", true);
});
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("OperarExecucao", p => p.RequireRole("Mecanico", "Administrador"));
    options.AddPolicy("ReexecutarExecucao", p => p.RequireRole("Administrador"));
    options.AddPolicy("AdministrarCatalogo", p => p.RequireRole("Administrador"));
    options.AddPolicy("RegistrarEntradaInsumo", p => p.RequireRole("Administrador", "Atendente"));
});

var app = builder.Build();
if (!app.Environment.IsEnvironment("Testing"))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<ExecucaoDbContext>().Database.MigrateAsync();
}
if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.Use(async (context, next) =>
{
    try { await next(); }
    catch (ConcorrenciaException) when (!context.Response.HasStarted)
    { context.Response.StatusCode = StatusCodes.Status409Conflict; await context.Response.WriteAsJsonAsync(new { codigo = "conflito_concorrencia" }); }
    catch (Exception ex) when (!context.Response.HasStarted && (ex is DependenciaIndisponivelException || FalhaTransitoria.EhTransitoria(ex)))
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable; context.Response.Headers.RetryAfter = "5";
        await context.Response.WriteAsJsonAsync(new { codigo = "dependencia_indisponivel", dependencia = (ex as DependenciaIndisponivelException)?.Dependencia ?? "banco" });
    }
});
app.UseAuthentication(); app.UseAuthorization();

static string? Identidade(ClaimsPrincipal user) =>
    user.FindFirst("sub")?.Value is { Length: > 0 } sub ? sub : user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

static object Projetar(OrdemExecucao o) => new
{
    o.Id,
    o.OrdemServicoId,
    o.ClienteId,
    o.VeiculoId,
    o.CorrelacaoId,
    status = o.Estado.ToString(),
    o.IntervencaoRequerida,
    o.MotivoFalha,
    o.CriadaEm,
    o.AtualizadaEm,
    o.ConcluidaEm,
    o.FalhouEm,
    o.CanceladaEm,
    o.MotivoCancelamento,
    etapas = o.Etapas.OrderBy(e => e.Sequencia).Select(e => new { e.Id, e.Sequencia, tipo = e.Tipo.ToString(), e.Descricao, e.Responsavel, e.RegistradaEm })
};

static async Task<IResult> Executar(Func<Task<IResult>> acao, string codigoInvalido)
{
    try { return await acao(); }
    catch (KeyNotFoundException) { return Results.NotFound(new { codigo = "execucao_nao_encontrada" }); }
    catch (TransicaoInvalidaException ex) { return Results.Conflict(new { codigo = "execucao_estado_invalido", detalhe = ex.Message }); }
    catch (ArgumentException) { return Results.UnprocessableEntity(new { codigo = codigoInvalido }); }
}

app.MapGet("/api/v1/fila-execucao", async (string[]? status, string? mecanico, ExecucaoService service, CancellationToken ct) =>
{
    var estados = new List<EstadoExecucao>();
    foreach (var valor in (status ?? []).SelectMany(s => s.Split(',', StringSplitOptions.TrimEntries)))
    {
        if (!Enum.TryParse<EstadoExecucao>(valor, true, out var estado) || !Enum.IsDefined(estado) || int.TryParse(valor, out _)) return Results.UnprocessableEntity(new { codigo = "filtro_invalido" });
        estados.Add(estado);
    }
    if (mecanico is not null && (string.IsNullOrWhiteSpace(mecanico) || mecanico.Length > LimitesExecucao.IdentidadeMaxima || mecanico.Contains((char)0)))
        return Results.UnprocessableEntity(new { codigo = "filtro_invalido" });
    var fila = await service.ListarFilaAsync(estados, mecanico?.Trim(), ct);
    return Results.Ok(new { total = fila.Count, itens = fila.Select(Projetar) });
}).RequireAuthorization("OperarExecucao");

var execucoes = app.MapGroup("/api/v1/execucoes");
execucoes.MapPost("/{id:guid}/etapas", async (Guid id, HttpContext context, ExecucaoService service, CancellationToken ct) =>
{
    var responsavel = Identidade(context.User);
    if (responsavel is null) return Results.Forbid();
    var corpo = await CorpoJson.LerObjetoAsync(context.Request, ct);
    if (corpo is null || !CorpoJson.TentarLerTipoEtapa(corpo.Value, out var tipo)
        || !LeitorJson.TentarLerTexto(corpo.Value, "descricao", LimitesExecucao.TextoMaximo, out var descricao))
        return Results.UnprocessableEntity(new { codigo = "etapa_invalida" });
    return await Executar(async () =>
    {
        var etapa = await service.RegistrarEtapaAsync(id, tipo, descricao!, responsavel, ct);
        return Results.Created($"/api/v1/execucoes/{id}/etapas/{etapa.Id}", new { etapa.Id, execucaoId = id, etapa.Sequencia, tipo = etapa.Tipo.ToString(), etapa.Descricao, etapa.Responsavel, etapa.RegistradaEm });
    }, "etapa_invalida");
}).RequireAuthorization("OperarExecucao");

execucoes.MapPost("/{id:guid}/conclusao", (Guid id, ExecucaoService service, CancellationToken ct) => Executar(async () =>
{
    var o = await service.ConcluirAsync(id, ct);
    return Results.Ok(new { o.Id, o.OrdemServicoId, status = o.Estado.ToString(), o.ConcluidaEm });
}, "conclusao_invalida")).RequireAuthorization("OperarExecucao");

execucoes.MapPost("/{id:guid}/falha", async (Guid id, HttpContext context, ExecucaoService service, CancellationToken ct) =>
{
    var corpo = await CorpoJson.LerObjetoAsync(context.Request, ct);
    if (corpo is null || !LeitorJson.TentarLerTexto(corpo.Value, "motivo", LimitesExecucao.TextoMaximo, out var motivo))
        return Results.UnprocessableEntity(new { codigo = "falha_invalida" });
    return await Executar(async () =>
    {
        var o = await service.FalharAsync(id, motivo!, ct);
        return Results.Ok(new { o.Id, o.OrdemServicoId, status = o.Estado.ToString(), o.IntervencaoRequerida, o.MotivoFalha });
    }, "falha_invalida");
}).RequireAuthorization("OperarExecucao");

execucoes.MapPost("/{id:guid}/reexecucao", async (Guid id, HttpContext context, ExecucaoService service, CancellationToken ct) =>
{
    var supervisor = Identidade(context.User);
    if (supervisor is null) return Results.Forbid();
    var corpo = await CorpoJson.LerObjetoAsync(context.Request, ct);
    if (corpo is null || !LeitorJson.TentarLerTexto(corpo.Value, "motivo", LimitesExecucao.TextoMaximo, out var motivo))
        return Results.UnprocessableEntity(new { codigo = "reexecucao_invalida" });
    return await Executar(async () =>
    {
        var o = await service.ReexecutarAsync(id, motivo!, supervisor, ct);
        return Results.Accepted($"/api/v1/fila-execucao", new { o.Id, o.OrdemServicoId, status = o.Estado.ToString(), o.IntervencaoRequerida, etapasPreservadas = o.Etapas.Count, supervisor });
    }, "reexecucao_invalida");
}).RequireAuthorization("ReexecutarExecucao");

static object ProjetarServico(ServicoCatalogo s) => new { s.Id, s.Nome, s.Descricao, s.PrecoUnitario, s.CriadoEm };
static object ProjetarInsumo(Insumo i) => new { i.Id, i.Nome, i.Descricao, i.PrecoUnitario, i.QuantidadeDisponivel, i.QuantidadeReservada, i.CriadoEm };

static bool FiltroNomeValido(string? nome) => nome is null || (!string.IsNullOrWhiteSpace(nome) && nome.Length <= LimitesCatalogo.NomeMaximo && !nome.Contains((char)0));

// Catálogo e insumos: leitura para qualquer usuário autenticado; criação só do Administrador; entrada de estoque do Administrador ou Atendente.
app.MapGet("/api/v1/servicos", async (string? nome, EstoqueService service, CancellationToken ct) =>
    FiltroNomeValido(nome) ? Results.Ok(new { itens = (await service.ListarServicosAsync(nome?.Trim(), ct)).Select(ProjetarServico) }) : Results.UnprocessableEntity(new { codigo = "filtro_invalido" })).RequireAuthorization();

app.MapPost("/api/v1/servicos", async (HttpContext context, EstoqueService service, CancellationToken ct) =>
{
    var corpo = await CorpoJson.LerObjetoAsync(context.Request, ct);
    if (corpo is null || !CorpoJson.TentarLerCatalogo(corpo.Value, out var nome, out var descricao, out var preco)) return Results.UnprocessableEntity(new { codigo = "servico_invalido" });
    return await Executar(async () =>
    {
        var servico = await service.CriarServicoAsync(nome!, descricao, preco, ct);
        return Results.Created($"/api/v1/servicos/{servico.Id}", ProjetarServico(servico));
    }, "servico_invalido");
}).RequireAuthorization("AdministrarCatalogo");

app.MapGet("/api/v1/insumos", async (string? nome, EstoqueService service, CancellationToken ct) =>
    FiltroNomeValido(nome) ? Results.Ok(new { itens = (await service.ListarInsumosAsync(nome?.Trim(), ct)).Select(ProjetarInsumo) }) : Results.UnprocessableEntity(new { codigo = "filtro_invalido" })).RequireAuthorization();

app.MapPost("/api/v1/insumos", async (HttpContext context, EstoqueService service, CancellationToken ct) =>
{
    var corpo = await CorpoJson.LerObjetoAsync(context.Request, ct);
    if (corpo is null || !CorpoJson.TentarLerCatalogo(corpo.Value, out var nome, out var descricao, out var preco)
        || !CorpoJson.TentarLerInteiroOpcional(corpo.Value, "quantidadeInicial", 0, out var quantidadeInicial)) return Results.UnprocessableEntity(new { codigo = "insumo_invalido" });
    return await Executar(async () =>
    {
        var insumo = await service.CriarInsumoAsync(nome!, descricao, preco, quantidadeInicial, ct);
        return Results.Created($"/api/v1/insumos/{insumo.Id}", ProjetarInsumo(insumo));
    }, "insumo_invalido");
}).RequireAuthorization("AdministrarCatalogo");

app.MapPost("/api/v1/insumos/{id:guid}/entradas", async (Guid id, HttpContext context, EstoqueService service, CancellationToken ct) =>
{
    var responsavel = Identidade(context.User);
    if (responsavel is null) return Results.Forbid();
    var corpo = await CorpoJson.LerObjetoAsync(context.Request, ct);
    if (corpo is null || !CorpoJson.TentarLerInteiro(corpo.Value, "quantidade", out var quantidade) || quantidade is < 1 or > LimitesCatalogo.QuantidadeMaxima)
        return Results.UnprocessableEntity(new { codigo = "entrada_invalida" });
    try { return Results.Ok(ProjetarInsumo(await service.RegistrarEntradaAsync(id, quantidade, responsavel, ct))); }
    catch (KeyNotFoundException) { return Results.NotFound(new { codigo = "insumo_nao_encontrado" }); }
    catch (ArgumentException) { return Results.UnprocessableEntity(new { codigo = "entrada_invalida" }); }
}).RequireAuthorization("RegistrarEntradaInsumo");

app.Run();
public partial class Program;
