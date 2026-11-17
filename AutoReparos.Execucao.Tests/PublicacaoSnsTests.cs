using Amazon;
using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using AutoReparos.Execucao.Application;
using AutoReparos.Execucao.Domain;
using AutoReparos.Execucao.Infrastructure;
using AutoReparos.Execucao.Tests.Suporte;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoReparos.Execucao.Tests;

/// <summary>C17 e C18 de infra-ci-observabilidade (D-67): com o tópico configurado, cada linha do outbox vira uma única publicação no SNS.</summary>
public sealed class PublicacaoSnsTests
{
    private const string Topico = "arn:aws:sns:us-east-1:198903849859:autoreparos-execucao-eventos";

    private static IConfiguration Configuracao() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Sns:EventosTopicArn"] = Topico,
        ["Sqs:OsServiceQueueUrl"] = FabricaApi.FilaOs,
        ["Sqs:BillingQueueUrl"] = FabricaApi.FilaBilling,
        ["Sqs:DeadLetterQueueUrl"] = FabricaApi.DlqSaida
    }).Build();

    private static MensagemSaida Saida(string destino, string tipo = TiposEvento.ExecucaoConcluida)
    {
        var eventId = Guid.NewGuid();
        return new MensagemSaida
        {
            Id = Guid.NewGuid(),
            EventId = eventId,
            Destino = destino,
            Tipo = tipo,
            Conteudo = $"{{\"eventId\":\"{eventId}\",\"tipo\":\"{tipo}\",\"ocorreuEm\":\"2026-11-16T12:00:00+00:00\"}}",
            CriadoEm = DateTimeOffset.UtcNow
        };
    }

    private static PublicadorSqsEventos Publicador(SqsFalso sqs, SnsFalso sns, AlertasFalsos alertas, EsperasRegistradas esperas) =>
        new(sqs, Configuracao(), NullLogger<PublicadorSqsEventos>.Instance, alertas, esperas.Politica(5, () => 5), sns);

    [Fact]
    public async Task EventoDoOutbox_PublicaUmaVezNoTopicoComOEnvelopeInalteradoEOAtributoTipo()
    {
        var sqs = new SqsFalso(); var sns = new SnsFalso(); var alertas = new AlertasFalsos();
        var mensagem = Saida(DestinosEvento.Topico);

        var resultado = await Publicador(sqs, sns, alertas, new EsperasRegistradas()).PublicarAsync(mensagem, CancellationToken.None);

        Assert.Equal(ResultadoPublicacao.Enviado, resultado);
        var publicacao = Assert.Single(sns.Publicadas);
        Assert.Equal(Topico, publicacao.TopicArn);
        Assert.Equal(mensagem.Conteudo, publicacao.Message);
        var tipo = Assert.Single(publicacao.MessageAttributes);
        Assert.Equal(("tipo", "String", TiposEvento.ExecucaoConcluida), (tipo.Key, tipo.Value.DataType, tipo.Value.StringValue));
        Assert.Empty(sqs.Enviadas);
        Assert.Empty(alertas.Itens);
    }

    [Theory]
    [InlineData(DestinosEvento.OsService)]
    [InlineData(DestinosEvento.Billing)]
    public async Task LinhaPendenteComDestinoAntigo_TambemVaiParaOTopico(string destino)
    {
        var sqs = new SqsFalso(); var sns = new SnsFalso();
        var mensagem = Saida(destino, TiposEvento.InsumosReservados);

        await Publicador(sqs, sns, new AlertasFalsos(), new EsperasRegistradas()).PublicarAsync(mensagem, CancellationToken.None);

        var publicacao = Assert.Single(sns.Publicadas);
        Assert.Equal((Topico, mensagem.Conteudo, TiposEvento.InsumosReservados), (publicacao.TopicArn, publicacao.Message, publicacao.MessageAttributes["tipo"].StringValue));
        Assert.Empty(sqs.Enviadas);
    }

    [Fact]
    public async Task FalhaTransitoriaNoSns_RepeteComBackoffEPublicaSemIrParaADlq()
    {
        var sqs = new SqsFalso(); var sns = new SnsFalso { Falha = tentativa => tentativa < 3 ? new TimeoutException() : null };
        var alertas = new AlertasFalsos(); var esperas = new EsperasRegistradas();

        var resultado = await Publicador(sqs, sns, alertas, esperas).PublicarAsync(Saida(DestinosEvento.Topico), CancellationToken.None);

        Assert.Equal(ResultadoPublicacao.Enviado, resultado);
        Assert.Equal(3, sns.Tentativas);
        Assert.Single(sns.Publicadas);
        Assert.Empty(sqs.Enviadas);
        Assert.Empty(alertas.Itens);
        Assert.Equal([105, 205], esperas.Itens.Select(e => (int)e.TotalMilliseconds).ToArray());
    }

    [Fact]
    public async Task SnsIndisponivel_EsgotaAsCincoTentativasEEntaoVaiParaADlqComAlerta()
    {
        var sqs = new SqsFalso(); var sns = new SnsFalso { Falha = _ => new InvalidOperationException("SNS fora do ar") };
        var alertas = new AlertasFalsos();
        var mensagem = Saida(DestinosEvento.Topico);

        var resultado = await Publicador(sqs, sns, alertas, new EsperasRegistradas()).PublicarAsync(mensagem, CancellationToken.None);

        Assert.Equal(ResultadoPublicacao.EncaminhadoParaDlq, resultado);
        Assert.Equal(5, sns.Tentativas);
        Assert.Empty(sns.Publicadas);
        Assert.Equal([(FabricaApi.DlqSaida, mensagem.Conteudo)], sqs.Enviadas);
        Assert.Equal([("execucao.sqs.exhausted", $"{DestinosEvento.Topico}/{mensagem.EventId}")], alertas.Itens);
    }

    private sealed class SnsFalso() : AmazonSimpleNotificationServiceClient(new AnonymousAWSCredentials(), RegionEndpoint.USEast1)
    {
        public List<PublishRequest> Publicadas { get; } = [];
        public int Tentativas { get; private set; }
        public Func<int, Exception?> Falha { get; init; } = _ => null;

        public override Task<PublishResponse> PublishAsync(PublishRequest request, CancellationToken cancellationToken = default)
        {
            Tentativas++;
            if (Falha(Tentativas) is { } falha) throw falha;
            Publicadas.Add(request);
            return Task.FromResult(new PublishResponse { MessageId = Guid.NewGuid().ToString() });
        }
    }
}
