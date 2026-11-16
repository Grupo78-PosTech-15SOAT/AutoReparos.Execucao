using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Amazon;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using AutoReparos.Execucao.Tests.Suporte;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AutoReparos.Execucao.Tests;

/// <summary>C12 de saga-compensacoes na Execução: <c>POST /api/v1/admin/dlq/reprocessar</c> devolve as mensagens das DLQs às filas de origem.</summary>
[Collection("postgres")]
public sealed class ReprocessaDlqTests(PostgresFixture postgres)
{
    private sealed class SqsDlqFalso() : AmazonSQSClient(new AnonymousAWSCredentials(), new AmazonSQSConfig { RegionEndpoint = RegionEndpoint.USEast1 })
    {
        public Dictionary<string, Queue<Message>> Dlqs { get; } = new() { [FabricaApi.DlqLiberadas] = new(), [FabricaApi.DlqSaida] = new() };
        public List<(string Fila, string Corpo)> Enviadas { get; } = [];
        public List<string> Apagadas { get; } = [];

        public override Task<ReceiveMessageResponse> ReceiveMessageAsync(ReceiveMessageRequest request, CancellationToken cancellationToken = default)
        {
            var lote = new List<Message>();
            if (Dlqs.TryGetValue(request.QueueUrl, out var fila)) while (fila.Count > 0 && lote.Count < request.MaxNumberOfMessages) lote.Add(fila.Dequeue());
            return Task.FromResult(new ReceiveMessageResponse { Messages = lote });
        }

        public override Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add((request.QueueUrl, request.MessageBody));
            return Task.FromResult(new SendMessageResponse());
        }

        public override Task<DeleteMessageResponse> DeleteMessageAsync(DeleteMessageRequest request, CancellationToken cancellationToken = default)
        {
            Apagadas.Add(request.ReceiptHandle);
            return Task.FromResult(new DeleteMessageResponse());
        }
    }

    private static Message Msg(string recibo, string tipo = "execucao.falhou.v1") => new() { ReceiptHandle = recibo, Body = $"{{\"eventId\":\"{Guid.NewGuid()}\",\"tipo\":\"{tipo}\"}}" };

    private async Task<(Ambiente Ambiente, SqsDlqFalso Sqs)> CriarAsync()
    {
        var sqs = new SqsDlqFalso();
        var ambiente = await Ambiente.CriarAsync(postgres, ajustar: services => { services.RemoveAll<IAmazonSQS>(); services.AddSingleton<IAmazonSQS>(sqs); });
        return (ambiente, sqs);
    }

    [Fact]
    public async Task ReprocessaDlqDaExecucao_DevolveCadaDlqAsFilasDeOrigemAteAQuantidadePedida()
    {
        var (ambiente, sqs) = await CriarAsync();
        using var _ = ambiente;
        sqs.Dlqs[FabricaApi.DlqLiberadas].Enqueue(Msg("l1", "ordem-servico.liberada.v1"));
        sqs.Dlqs[FabricaApi.DlqSaida].Enqueue(Msg("s1", "execucao.concluida.v1"));
        sqs.Dlqs[FabricaApi.DlqSaida].Enqueue(Msg("s2"));
        sqs.Dlqs[FabricaApi.DlqSaida].Enqueue(Msg("s3"));
        using var admin = ambiente.Fabrica.Cliente("Administrador", "adm-1");

        var resposta = await admin.PostAsJsonAsync("/api/v1/admin/dlq/reprocessar", new { quantidade = 3 });

        Assert.Equal(HttpStatusCode.Accepted, resposta.StatusCode);
        var corpo = await resposta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((3, 3), (corpo.GetProperty("solicitadas").GetInt32(), corpo.GetProperty("reprocessadas").GetInt32()));
        // A concluída vai ao OS e à Cobrança (mesmo eventId nos dois); as demais só ao OS.
        Assert.Equal([FabricaApi.FilaLiberadas, FabricaApi.FilaOs, FabricaApi.FilaBilling, FabricaApi.FilaOs], sqs.Enviadas.Select(e => e.Fila));
        Assert.Equal(["l1", "s1", "s2"], sqs.Apagadas);
        Assert.Single(sqs.Dlqs[FabricaApi.DlqSaida]);
        var alerta = Assert.Single(ambiente.Alertas.Itens);
        Assert.Equal("execucao.dlq-reprocessada", alerta.Codigo);
        Assert.Contains("adm-1", alerta.Detalhe);
    }

    [Theory]
    [InlineData(null, "{\"quantidade\":1}", HttpStatusCode.Unauthorized)]
    [InlineData("Atendente", "{\"quantidade\":1}", HttpStatusCode.Forbidden)]
    [InlineData("Administrador", "{\"quantidade\":0}", HttpStatusCode.UnprocessableEntity)]
    [InlineData("Administrador", "{\"quantidade\":101}", HttpStatusCode.UnprocessableEntity)]
    [InlineData("Administrador", "{}", HttpStatusCode.UnprocessableEntity)]
    public async Task ReprocessaDlqDaExecucao_RecusaPapelEQuantidadeInvalidos(string? papel, string corpo, HttpStatusCode esperado)
    {
        var (ambiente, sqs) = await CriarAsync();
        using var _ = ambiente;
        sqs.Dlqs[FabricaApi.DlqLiberadas].Enqueue(Msg("l1"));
        using var cliente = ambiente.Fabrica.Cliente(papel, "adm-1");

        var resposta = await cliente.PostAsync("/api/v1/admin/dlq/reprocessar", Mensagens.Json(corpo));

        Assert.Equal(esperado, resposta.StatusCode);
        Assert.Empty(sqs.Enviadas);
        Assert.Single(sqs.Dlqs[FabricaApi.DlqLiberadas]);
    }
}
