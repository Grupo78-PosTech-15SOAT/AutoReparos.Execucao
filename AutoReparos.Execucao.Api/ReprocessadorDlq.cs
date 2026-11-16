using Amazon.SQS;
using Amazon.SQS.Model;
using AutoReparos.Execucao.Application;

namespace AutoReparos.Execucao.Api;

public sealed record ResultadoReprocessamentoDlq(int Solicitadas, int Reprocessadas);

/// <summary>
/// Reprocessa as DLQs da Execução. A de entrada (<c>Sqs:LiberadasDeadLetterQueueUrl</c>) volta à fila de entrada
/// (<c>Sqs:LiberadasQueueUrl</c>). A de eventos publicados (<c>Sqs:DeadLetterQueueUrl</c>) volta às filas de destino pelo <c>tipo</c>:
/// <c>execucao.concluida.v1</c> vai ao OS e à Cobrança (cada um deduplica pelo mesmo <c>eventId</c>) e os demais só ao OS. A mensagem
/// só sai da DLQ depois de reenviada; a ação é registrada com o autor (alerta <c>execucao.dlq-reprocessada</c>).
/// </summary>
public sealed class ReprocessadorDlq(IAmazonSQS sqs, IConfiguration configuration, IAlertSink alerts)
{
    public const int QuantidadeMaxima = 100;

    public async Task<ResultadoReprocessamentoDlq> ReprocessarAsync(int quantidade, string autor, CancellationToken ct)
    {
        var reprocessadas = 0;
        if (configuration["Sqs:LiberadasDeadLetterQueueUrl"] is { Length: > 0 } dlqEntrada)
            reprocessadas += await DevolverAsync(dlqEntrada, _ => [Exigir("Sqs:LiberadasQueueUrl")], quantidade, ct);
        if (reprocessadas < quantidade && configuration["Sqs:DeadLetterQueueUrl"] is { Length: > 0 } dlqSaida)
            reprocessadas += await DevolverAsync(dlqSaida, DestinosDeEvento, quantidade - reprocessadas, ct);
        await alerts.AlertarAsync("execucao.dlq-reprocessada", $"DLQ da Execução reprocessada por {autor}: {reprocessadas} de {quantidade} solicitadas devolvidas à fila de origem.", ct);
        return new ResultadoReprocessamentoDlq(quantidade, reprocessadas);
    }

    private string[] DestinosDeEvento(string? corpo) =>
        LeitorJson.TentarLerObjeto(corpo, out var json) && LeitorJson.TentarLerTexto(json, "tipo", 100, out var tipo) && tipo == TiposEvento.ExecucaoConcluida
            ? [Exigir("Sqs:OsServiceQueueUrl"), Exigir("Sqs:BillingQueueUrl")]
            : [Exigir("Sqs:OsServiceQueueUrl")];

    private async Task<int> DevolverAsync(string dlq, Func<string?, string[]> destinos, int limite, CancellationToken ct)
    {
        var devolvidas = 0;
        while (devolvidas < limite)
        {
            var resposta = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = dlq, MaxNumberOfMessages = Math.Min(10, limite - devolvidas), WaitTimeSeconds = 0 }, ct);
            if (resposta.Messages is not { Count: > 0 } mensagens) break;
            foreach (var mensagem in mensagens)
            {
                foreach (var fila in destinos(mensagem.Body)) await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = fila, MessageBody = mensagem.Body }, ct);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = dlq, ReceiptHandle = mensagem.ReceiptHandle }, ct);
                devolvidas++;
            }
        }
        return devolvidas;
    }

    private string Exigir(string chave) =>
        configuration[chave] is { Length: > 0 } valor ? valor : throw new InvalidOperationException($"{chave} não configurada.");
}
