using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AutoReparos.Execucao.Application;
using AutoReparos.Execucao.Domain;
using AutoReparos.Execucao.Infrastructure;
using AutoReparos.Execucao.Tests.Suporte;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace AutoReparos.Execucao.Tests;

[Collection("postgres")]
public sealed class ResilienciaTests(PostgresFixture postgres)
{
    private static readonly int[] EsperaBaseMs = [100, 200, 400, 800];

    private static IConfiguration Configuracao() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Sqs:OsServiceQueueUrl"] = FabricaApi.FilaOs,
        ["Sqs:BillingQueueUrl"] = FabricaApi.FilaBilling,
        ["Sqs:DeadLetterQueueUrl"] = FabricaApi.DlqSaida
    }).Build();

    private static MensagemSaida Saida(string destino = DestinosEvento.OsService) => new()
    {
        Id = Guid.NewGuid(),
        EventId = Guid.NewGuid(),
        Destino = destino,
        Tipo = "execucao.concluida.v1",
        Conteudo = "{\"eventId\":\"x\"}",
        CriadoEm = DateTimeOffset.UtcNow
    };

    private static Exception FalhaDeRede() => new NpgsqlException("conexão perdida", new IOException("reset"));

    private static void AssertEsperasCrescentesComJitterLimitado(IReadOnlyList<TimeSpan> esperas)
    {
        Assert.Equal(4, esperas.Count);
        for (var i = 0; i < esperas.Count; i++)
        {
            var ms = esperas[i].TotalMilliseconds;
            Assert.InRange(ms, EsperaBaseMs[i], EsperaBaseMs[i] + 99);
            if (i > 0) Assert.True(ms > esperas[i - 1].TotalMilliseconds, $"A espera {i + 1} deveria superar a anterior.");
        }
    }

    [Fact]
    public async Task DependencyRetryExhaustion_FiveAttemptsWithExponentialBackoffAndJitter()
    {
        var esperas = new EsperasRegistradas(); int tentativas = 0, esgotado = 0;
        await Assert.ThrowsAsync<TimeoutException>(() => DependencyRetry.ExecuteAsync(
            _ => { tentativas++; throw new TimeoutException("banco"); }, _ => { esgotado++; return Task.CompletedTask; },
            CancellationToken.None, esperas.Politica(5, () => 37), FalhaTransitoria.EhTransitoria));
        Assert.Equal(5, tentativas);
        Assert.Equal(1, esgotado);
        Assert.Equal([137, 237, 437, 837], esperas.Itens.Select(e => (int)e.TotalMilliseconds).ToArray());
    }

    [Fact]
    public async Task DependencyRetryExhaustion_RandomJitterIsBoundedAndDelaysGrow()
    {
        for (var rodada = 0; rodada < 25; rodada++)
        {
            var esperas = new EsperasRegistradas();
            await Assert.ThrowsAsync<InvalidOperationException>(() => DependencyRetry.ExecuteAsync(
                _ => throw new InvalidOperationException("sqs"), _ => Task.CompletedTask, CancellationToken.None, esperas.Politica()));
            AssertEsperasCrescentesComJitterLimitado(esperas.Itens);
        }
    }

    [Fact]
    public async Task DependencyRetryExhaustion_NonTransientFailureIsNotRetried()
    {
        var esperas = new EsperasRegistradas(); int tentativas = 0, esgotado = 0;
        await Assert.ThrowsAsync<ArgumentException>(() => DependencyRetry.ExecuteAsync(
            _ => { tentativas++; throw new ArgumentException("bug"); }, _ => { esgotado++; return Task.CompletedTask; },
            CancellationToken.None, esperas.Politica(), FalhaTransitoria.EhTransitoria));
        Assert.Equal((1, 0, 0), (tentativas, esgotado, esperas.Itens.Count));
    }

    [Fact]
    public async Task DependencyRetryExhaustion_RecoveryBeforeTheLimitSkipsExhaustionHandling()
    {
        var esperas = new EsperasRegistradas(); int tentativas = 0, esgotado = 0;
        await DependencyRetry.ExecuteAsync(_ => { if (++tentativas < 3) throw new TimeoutException(); return Task.CompletedTask; },
            _ => { esgotado++; return Task.CompletedTask; }, CancellationToken.None, esperas.Politica(5, () => 0));
        Assert.Equal((3, 0), (tentativas, esgotado));
        Assert.Equal([100, 200], esperas.Itens.Select(e => (int)e.TotalMilliseconds).ToArray());
    }

    [Fact]
    public void DependencyRetryExhaustion_DelayPolicyHasCeilingAndRejectsInvalidArguments()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(5000), RetryDelayPolicy.Calculate(30, 99));
        Assert.Equal(TimeSpan.FromMilliseconds(100), RetryDelayPolicy.Calculate(1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => RetryDelayPolicy.Calculate(1, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => RetryDelayPolicy.Calculate(0, 0));
    }

    [Fact]
    public async Task DependencyRetryExhaustion_SqsPublisherSendsToDeadLetterAndAlertsAfterFiveFailures()
    {
        var sqs = new SqsFalso { FalhaEnvio = (fila, _) => fila == FabricaApi.FilaOs ? new InvalidOperationException("fila indisponível") : null };
        var alertas = new AlertasFalsos(); var esperas = new EsperasRegistradas();
        var publicador = new PublicadorSqsEventos(sqs, Configuracao(), NullLogger<PublicadorSqsEventos>.Instance, alertas, esperas.Politica());
        var mensagem = Saida();

        var resultado = await publicador.PublicarAsync(mensagem, CancellationToken.None);

        Assert.Equal(ResultadoPublicacao.EncaminhadoParaDlq, resultado);
        Assert.Equal(5, sqs.Tentativas(FabricaApi.FilaOs));
        Assert.Equal(1, sqs.Tentativas(FabricaApi.DlqSaida));
        Assert.Equal([(FabricaApi.DlqSaida, mensagem.Conteudo)], sqs.Enviadas);
        Assert.Equal([("execucao.sqs.exhausted", $"{DestinosEvento.OsService}/{mensagem.EventId}")], alertas.Itens);
        AssertEsperasCrescentesComJitterLimitado(esperas.Itens);
    }

    [Fact]
    public async Task DependencyRetryExhaustion_SqsPublisherKeepsMessagePendingWhenDeadLetterAlsoFails()
    {
        var sqs = new SqsFalso { FalhaEnvio = (_, _) => new InvalidOperationException("SQS fora do ar") };
        var alertas = new AlertasFalsos(); var esperas = new EsperasRegistradas();
        var publicador = new PublicadorSqsEventos(sqs, Configuracao(), NullLogger<PublicadorSqsEventos>.Instance, alertas, esperas.Politica());
        var mensagem = Saida(DestinosEvento.Billing);

        await Assert.ThrowsAsync<InvalidOperationException>(() => publicador.PublicarAsync(mensagem, CancellationToken.None));

        Assert.Equal(5, sqs.Tentativas(FabricaApi.FilaBilling));
        Assert.Equal(1, sqs.Tentativas(FabricaApi.DlqSaida));
        Assert.Empty(sqs.Enviadas);
        Assert.Equal([("execucao.sqs.dlq_failed", $"{DestinosEvento.Billing}/{mensagem.EventId}")], alertas.Itens);
    }

    [Fact]
    public async Task DependencyRetryExhaustion_SqsPublisherRecoversWithoutDeadLetterOrAlert()
    {
        var sqs = new SqsFalso { FalhaEnvio = (fila, tentativa) => fila == FabricaApi.FilaBilling && tentativa < 3 ? new TimeoutException() : null };
        var alertas = new AlertasFalsos(); var esperas = new EsperasRegistradas();
        var publicador = new PublicadorSqsEventos(sqs, Configuracao(), NullLogger<PublicadorSqsEventos>.Instance, alertas, esperas.Politica(5, () => 5));
        var mensagem = Saida(DestinosEvento.Billing);

        Assert.Equal(ResultadoPublicacao.Enviado, await publicador.PublicarAsync(mensagem, CancellationToken.None));

        Assert.Equal(3, sqs.Tentativas(FabricaApi.FilaBilling));
        Assert.Equal([(FabricaApi.FilaBilling, mensagem.Conteudo)], sqs.Enviadas);
        Assert.Empty(alertas.Itens);
        Assert.Equal([105, 205], esperas.Itens.Select(e => (int)e.TotalMilliseconds).ToArray());
    }

    [Fact]
    public async Task DependencyRetryExhaustion_OutboxDispatcherMarksDeadLetterOrSchedulesRetry()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var enviada = Saida(); var pendente = Saida(DestinosEvento.Billing);
        pendente.CriadoEm = enviada.CriadoEm.AddSeconds(1);
        await ambiente.Fabrica.NoBancoAsync(async db => { db.MensagensSaida.AddRange(enviada, pendente); await db.SaveChangesAsync(); return 0; });
        // A fila do OS falha sempre, mas a DLQ aceita; a fila do Billing e a DLQ falham.
        ambiente.Sqs.FalhaEnvio = (fila, _) => fila == FabricaApi.DlqSaida && ambiente.Sqs.Tentativas(FabricaApi.FilaBilling) > 0 ? new InvalidOperationException("DLQ fora") :
            fila is FabricaApi.FilaOs or FabricaApi.FilaBilling ? new InvalidOperationException("fila fora") : null;

        Assert.True(await ambiente.Despachante.DespacharProximaAsync(CancellationToken.None));
        Assert.True(await ambiente.Despachante.DespacharProximaAsync(CancellationToken.None));
        Assert.False(await ambiente.Despachante.DespacharProximaAsync(CancellationToken.None));

        var linhas = (await ambiente.Fabrica.NoBancoAsync(db => db.MensagensSaida.AsNoTracking().ToListAsync())).ToDictionary(m => m.Id);
        var a = linhas[enviada.Id]; var b = linhas[pendente.Id];
        Assert.NotNull(a.EnviadoEm);
        Assert.True(a.EncaminhadoParaDlq);
        Assert.Equal(0, a.Tentativas);
        Assert.Null(b.EnviadoEm);
        Assert.False(b.EncaminhadoParaDlq);
        Assert.Equal(1, b.Tentativas);
        Assert.True(b.ProximaTentativaEm > DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Equal(new[] { "execucao.sqs.dlq_failed", "execucao.sqs.exhausted" }, ambiente.Alertas.Itens.Select(i => i.Codigo).Order().ToArray());
    }

    private sealed class Contador { public int Chamadas; }

    // Delega ao banco real, mas falha com erro de rede nas primeiras chamadas de leitura de eventos.
    private sealed class ArmazenamentoInstavel(ExecucaoDbContext real, Contador contador, int falhas, Func<Exception> erro) : IExecucaoStore
    {
        public Task<bool> EventoProcessadoAsync(Guid eventId, CancellationToken ct)
        {
            if (Interlocked.Increment(ref contador.Chamadas) <= falhas) throw erro();
            return real.EventoProcessadoAsync(eventId, ct);
        }
        public Task<OrdemExecucao?> ObterAsync(Guid id, CancellationToken ct) => real.ObterAsync(id, ct);
        public Task<IReadOnlyList<OrdemExecucao>> ListarFilaAsync(IReadOnlyCollection<EstadoExecucao> estados, string? mecanico, CancellationToken ct) => real.ListarFilaAsync(estados, mecanico, ct);
        public Task<OrdemExecucao?> ObterPorOrdemServicoAsync(Guid ordemServicoId, CancellationToken ct) => real.ObterPorOrdemServicoAsync(ordemServicoId, ct);
        public void Adicionar(OrdemExecucao ordem) => real.Adicionar(ordem);
        public void Adicionar(EventoProcessado evento) => real.Adicionar(evento);
        public void Adicionar(MensagemSaida mensagem) => real.Adicionar(mensagem);
        public void DescartarAlteracoes() => real.DescartarAlteracoes();
        public Task SalvarAsync(CancellationToken ct) => real.SalvarAsync(ct);
    }

    private async Task<(Ambiente Ambiente, Contador Contador)> AmbienteComBancoInstavelAsync(int falhas, Func<Exception> erro)
    {
        var contador = new Contador();
        var ambiente = await Ambiente.CriarAsync(postgres, ajustar: services => services.AddScoped<IExecucaoStore>(sp =>
            new ArmazenamentoInstavel(sp.GetRequiredService<ExecucaoDbContext>(), contador, falhas, erro)));
        return (ambiente, contador);
    }

    [Fact]
    public async Task DependencyRetryExhaustion_ConsumerForwardsEventToDeadLetterAndAlertsWhenDatabaseStaysDown()
    {
        var (ambiente, contador) = await AmbienteComBancoInstavelAsync(int.MaxValue, FalhaDeRede);
        using var liberar = ambiente;
        var eventId = Guid.NewGuid(); var mensagem = Mensagens.Liberada(eventId, Guid.NewGuid());

        await ambiente.Consumidor.ProcessarAsync(mensagem, CancellationToken.None);

        Assert.Equal(5, contador.Chamadas);
        AssertEsperasCrescentesComJitterLimitado(ambiente.Esperas.Itens);
        Assert.Equal([(FabricaApi.DlqLiberadas, mensagem.Body)], ambiente.Sqs.Enviadas);
        var alerta = Assert.Single(ambiente.Alertas.Itens);
        Assert.Equal("execucao.banco.exhausted", alerta.Codigo);
        Assert.Contains(eventId.ToString(), alerta.Detalhe);
        Assert.Equal([(FabricaApi.FilaLiberadas, mensagem.ReceiptHandle)], ambiente.Sqs.Removidas);
        Assert.Equal(0, await ambiente.Fabrica.NoBancoAsync(db => db.Ordens.CountAsync()));
    }

    [Fact]
    public async Task DependencyRetryExhaustion_ConsumerRecoversWhenDatabaseReturnsBeforeTheLimit()
    {
        var (ambiente, contador) = await AmbienteComBancoInstavelAsync(2, () => new TimeoutException("banco lento"));
        using var liberar = ambiente;
        var mensagem = Mensagens.Liberada(Guid.NewGuid(), Guid.NewGuid());

        await ambiente.Consumidor.ProcessarAsync(mensagem, CancellationToken.None);

        Assert.Equal(3, contador.Chamadas);
        Assert.Equal(2, ambiente.Esperas.Itens.Count);
        Assert.Equal(1, await ambiente.Fabrica.NoBancoAsync(db => db.Ordens.CountAsync()));
        Assert.Empty(ambiente.Sqs.Enviadas);
        Assert.Empty(ambiente.Alertas.Itens);
        Assert.Equal([(FabricaApi.FilaLiberadas, mensagem.ReceiptHandle)], ambiente.Sqs.Removidas);
    }

    [Fact]
    public async Task DependencyRetryExhaustion_ConsumerLeavesMessageInQueueWhenDeadLetterAlsoFails()
    {
        var (ambiente, _) = await AmbienteComBancoInstavelAsync(int.MaxValue, FalhaDeRede);
        using var liberar = ambiente;
        ambiente.Sqs.FalhaEnvio = (_, _) => new InvalidOperationException("DLQ fora");
        var mensagem = Mensagens.Liberada(Guid.NewGuid(), Guid.NewGuid());

        await Assert.ThrowsAsync<InvalidOperationException>(() => ambiente.Consumidor.ProcessarAsync(mensagem, CancellationToken.None));

        Assert.Empty(ambiente.Sqs.Removidas);
        Assert.Equal(["execucao.banco.exhausted", "execucao.entrada.dlq_failed"], ambiente.Alertas.Itens.Select(i => i.Codigo).ToArray());
    }

    [Fact]
    public async Task DependencyRetryExhaustion_ConsumerDoesNotRetryOrDeadLetterNonTransientFailures()
    {
        var (ambiente, contador) = await AmbienteComBancoInstavelAsync(int.MaxValue, () => new InvalidOperationException("defeito de programação"));
        using var liberar = ambiente;
        var mensagem = Mensagens.Liberada(Guid.NewGuid(), Guid.NewGuid());

        await Assert.ThrowsAsync<InvalidOperationException>(() => ambiente.Consumidor.ProcessarAsync(mensagem, CancellationToken.None));

        Assert.Equal(1, contador.Chamadas);
        Assert.Empty(ambiente.Esperas.Itens);
        Assert.Empty(ambiente.Sqs.Enviadas);
        Assert.Empty(ambiente.Sqs.Removidas);
    }

    [Fact]
    public async Task DependencyRetryExhaustion_UnreachableDatabaseIsTransientForConsumerAndReturns503ForApi()
    {
        const string inalcancavel = "Host=127.0.0.1;Port=1;Database=x;Username=u;Password=p;Timeout=2;Pooling=false";
        using var ambiente = await Ambiente.CriarAsync(postgres, conexao: inalcancavel);

        var mensagem = Mensagens.Liberada(Guid.NewGuid(), Guid.NewGuid());
        await ambiente.Consumidor.ProcessarAsync(mensagem, CancellationToken.None);
        Assert.Equal(4, ambiente.Esperas.Itens.Count);
        Assert.Equal([(FabricaApi.DlqLiberadas, mensagem.Body)], ambiente.Sqs.Enviadas);
        Assert.Equal("execucao.banco.exhausted", Assert.Single(ambiente.Alertas.Itens).Codigo);

        using var http = ambiente.Fabrica.Cliente("Mecanico");
        var resposta = await http.GetAsync("/api/v1/fila-execucao");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resposta.StatusCode);
        Assert.Equal("5", resposta.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ToString());
        var corpo = await resposta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("dependencia_indisponivel", "banco"), (corpo.GetProperty("codigo").GetString(), corpo.GetProperty("dependencia").GetString()));
    }
}
