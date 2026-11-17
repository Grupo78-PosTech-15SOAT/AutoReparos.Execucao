using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoReparos.Execucao.Application;
using AutoReparos.Execucao.Domain;
using AutoReparos.Execucao.Tests.Suporte;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AutoReparos.Execucao.Tests;

// Propagação do trace W3C pelo envelope das mensagens: criação (Outbox), publicação e consumo.
[Collection("postgres")]
public sealed class TraceMensageriaTests : IDisposable
{
    private const string FonteMensageria = "AutoReparos.Execucao.Mensageria";
    private const string NomeFonteDeTeste = "AutoReparos.Execucao.Tests.Trace";
    private static readonly ActivitySource FonteDeTeste = new(NomeFonteDeTeste);
    private readonly PostgresFixture _postgres;
    private readonly ConcurrentQueue<Activity> _encerradas = new();
    private readonly ActivityListener _ouvinte;

    public TraceMensageriaTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _ouvinte = new ActivityListener
        {
            ShouldListenTo = fonte => fonte.Name == FonteMensageria || fonte.Name == NomeFonteDeTeste,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _encerradas.Enqueue
        };
        ActivitySource.AddActivityListener(_ouvinte);
    }

    public void Dispose() => _ouvinte.Dispose();

    private static Activity IniciarOrigem(string? tracestate = null)
    {
        Activity.Current = null;
        var origem = FonteDeTeste.StartActivity("origem")!;
        origem.TraceStateString = tracestate;
        return origem;
    }

    private static async Task<List<JsonElement>> EnvelopesAsync(Ambiente ambiente) =>
        (await ambiente.Fabrica.NoBancoAsync(db => db.MensagensSaida.AsNoTracking().ToListAsync())).Select(m => JsonDocument.Parse(m.Conteudo).RootElement).ToList();

    private static async Task ConcluirAsync(Ambiente ambiente, Guid id)
    {
        using var scope = ambiente.Fabrica.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ExecucaoService>().ConcluirAsync(id, CancellationToken.None);
    }

    private static Task<OrdemExecucao> SemearEmExecucaoAsync(Ambiente ambiente) =>
        ambiente.Fabrica.SemearAsync(o => o.RegistrarEtapa(TipoEtapa.Reparo, "Troca de óleo", "mec-1", DateTimeOffset.UtcNow));

    private static Amazon.SQS.Model.Message ComCampo(Amazon.SQS.Model.Message mensagem, string nome, string valor)
    {
        var corpo = JsonNode.Parse(mensagem.Body)!.AsObject();
        corpo[nome] = valor;
        mensagem.Body = corpo.ToJsonString();
        return mensagem;
    }

    private Activity UnicaDaMensageria(ActivityKind tipo, Guid ordemServicoId) =>
        Assert.Single(_encerradas, a => a.Source.Name == FonteMensageria && a.Kind == tipo && (string?)a.GetTagItem("ordem_servico.id") == ordemServicoId.ToString());

    [Fact]
    public async Task OutboxEnvelope_CarriesTraceparentAndTracestateOfCurrentActivity()
    {
        using var ambiente = await Ambiente.CriarAsync(_postgres);
        var ordem = await SemearEmExecucaoAsync(ambiente);
        using var origem = IniciarOrigem("fornecedor=valor");

        await ConcluirAsync(ambiente, ordem.Id);

        var envelopes = await EnvelopesAsync(ambiente);
        Assert.Single(envelopes);
        Assert.Equal(ActivityIdFormat.W3C, origem.IdFormat);
        Assert.All(envelopes, envelope =>
        {
            Assert.Equal("execucao.concluida.v1", envelope.GetProperty("tipo").GetString());
            Assert.Equal(origem.Id, envelope.GetProperty("traceparent").GetString());
            Assert.Equal("fornecedor=valor", envelope.GetProperty("tracestate").GetString());
        });
    }

    [Fact]
    public async Task OutboxEnvelope_HasNoTraceparentWithoutCurrentActivity()
    {
        using var ambiente = await Ambiente.CriarAsync(_postgres);
        var ordem = await SemearEmExecucaoAsync(ambiente);
        Activity.Current = null;

        await ConcluirAsync(ambiente, ordem.Id);

        var envelopes = await EnvelopesAsync(ambiente);
        Assert.Single(envelopes);
        Assert.All(envelopes, envelope =>
        {
            Assert.False(envelope.TryGetProperty("traceparent", out _));
            Assert.False(envelope.TryGetProperty("tracestate", out _));
            Assert.Equal(ordem.OrdemServicoId, envelope.GetProperty("ordemServicoId").GetGuid());
        });
    }

    [Fact]
    public async Task Publisher_RecordsProducerSpanInTheTraceOfTheEnvelope()
    {
        using var ambiente = await Ambiente.CriarAsync(_postgres);
        var ordem = await SemearEmExecucaoAsync(ambiente);
        ActivityContext contexto;
        using (var origem = IniciarOrigem())
        {
            contexto = origem.Context;
            using var scope = ambiente.Fabrica.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ExecucaoService>().FalharAsync(ordem.Id, "Peça em falta", CancellationToken.None);
        }
        Activity.Current = null;
        var linha = Assert.Single(await ambiente.Fabrica.NoBancoAsync(db => db.MensagensSaida.AsNoTracking().ToListAsync()));

        Assert.True(await ambiente.Despachante.DespacharProximaAsync(CancellationToken.None));

        var span = UnicaDaMensageria(ActivityKind.Producer, ordem.OrdemServicoId);
        Assert.Equal("publicar execucao.falhou.v1", span.OperationName);
        Assert.Equal(contexto.TraceId, span.TraceId);
        Assert.Equal(contexto.SpanId, span.ParentSpanId);
        Assert.Equal("aws_sqs", span.GetTagItem("messaging.system"));
        Assert.Equal(linha.EventId.ToString(), span.GetTagItem("messaging.message.id"));
        Assert.Equal([(FabricaApi.FilaOs, linha.Conteudo)], ambiente.Sqs.Enviadas);
    }

    [Fact]
    public async Task Consumer_ProcessesInsideConsumerSpanChildOfEnvelopeAndPropagatesTheTrace()
    {
        using var ambiente = await Ambiente.CriarAsync(_postgres);
        var ordem = await ambiente.Fabrica.SemearAsync();
        var remoto = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded, isRemote: true);
        var mensagem = ComCampo(Mensagens.Cancelar(Guid.NewGuid(), ordem.OrdemServicoId), "traceparent", $"00-{remoto.TraceId}-{remoto.SpanId}-01");
        // Uma Activity ambiente (a do laço de consumo) não pode virar pai: o pai é o contexto do envelope.
        using var ambienteDoLaco = IniciarOrigem();

        await ambiente.Consumidor.ProcessarAsync(mensagem, CancellationToken.None);

        var span = UnicaDaMensageria(ActivityKind.Consumer, ordem.OrdemServicoId);
        Assert.Equal("processar execucao.cancelar.v1", span.OperationName);
        Assert.Equal(remoto.TraceId, span.TraceId);
        Assert.Equal(remoto.SpanId, span.ParentSpanId);
        var envelope = Assert.Single(await EnvelopesAsync(ambiente));
        Assert.Equal("execucao.cancelada.v1", envelope.GetProperty("tipo").GetString());
        Assert.Equal(span.Id, envelope.GetProperty("traceparent").GetString());
        Assert.True(ActivityContext.TryParse(envelope.GetProperty("traceparent").GetString(), null, out var gravado));
        Assert.Equal(remoto.TraceId, gravado.TraceId);
        Assert.Empty(ambiente.Sqs.Enviadas);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("traceparent", "abc")]
    [InlineData("campoDesconhecido", "qualquer")]
    public async Task Consumer_ProcessesMessageWithoutValidTraceparentInNewRootSpanWithoutDlq(string? campo, string? valor)
    {
        using var ambiente = await Ambiente.CriarAsync(_postgres);
        var ordem = await ambiente.Fabrica.SemearAsync();
        var mensagem = Mensagens.Cancelar(Guid.NewGuid(), ordem.OrdemServicoId);
        if (campo is not null) ComCampo(mensagem, campo, valor!);
        using var ambienteDoLaco = IniciarOrigem();

        await ambiente.Consumidor.ProcessarAsync(mensagem, CancellationToken.None);

        var span = UnicaDaMensageria(ActivityKind.Consumer, ordem.OrdemServicoId);
        Assert.Equal("processar execucao.cancelar.v1", span.OperationName);
        Assert.Equal(default, span.ParentSpanId);
        Assert.Null(span.Parent);
        Assert.NotEqual(ambienteDoLaco.TraceId, span.TraceId);
        Assert.Equal(EstadoExecucao.Cancelada, (await ambiente.Fabrica.NoBancoAsync(db => db.Ordens.AsNoTracking().SingleAsync())).Estado);
        var envelope = Assert.Single(await EnvelopesAsync(ambiente));
        Assert.Equal("execucao.cancelada.v1", envelope.GetProperty("tipo").GetString());
        Assert.Equal(span.Id, envelope.GetProperty("traceparent").GetString());
        Assert.Equal([(FabricaApi.FilaLiberadas, mensagem.ReceiptHandle)], ambiente.Sqs.Removidas);
        Assert.Empty(ambiente.Sqs.Enviadas);
        Assert.Empty(ambiente.Alertas.Itens);
    }
}
