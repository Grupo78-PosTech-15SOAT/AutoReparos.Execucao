using Amazon.SQS;
using Amazon.SQS.Model;
using AutoReparos.Execution.Application;
using AutoReparos.Execution.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace AutoReparos.Execution.Infrastructure;

public sealed class LoggingAlertSink(ILogger<LoggingAlertSink> logger) : IAlertSink
{
    public Task AlertarAsync(string codigo, string detalhe, CancellationToken ct) { logger.LogCritical("ALERTA {Codigo}: {Detalhe}", codigo, detalhe); return Task.CompletedTask; }
}

public static class RetryDelayPolicy
{
    public static TimeSpan Calculate(int attempt, int jitterMilliseconds)
    {
        if (attempt < 1 || jitterMilliseconds is < 0 or >= 100) throw new ArgumentOutOfRangeException();
        return TimeSpan.FromMilliseconds(Math.Min(5000L, 100L * (1L << Math.Min(attempt - 1, 30)) + jitterMilliseconds));
    }
}

// Limite padrão por dependência: 5 tentativas, backoff exponencial (100 ms * 2^(n-1), teto de 5 s) mais jitter de 0 a 99 ms.
// Espera e jitter são injetáveis para que os testes provem os intervalos sem dormir.
public sealed class PoliticaRetentativa(int maxTentativas = 5, Func<TimeSpan, CancellationToken, Task>? aguardar = null, Func<int>? jitterMilissegundos = null)
{
    public static PoliticaRetentativa Padrao { get; } = new();
    public int MaxTentativas { get; } = maxTentativas;
    public Task AguardarAsync(int tentativa, CancellationToken ct) =>
        (aguardar ?? Task.Delay).Invoke(RetryDelayPolicy.Calculate(tentativa, (jitterMilissegundos ?? (() => Random.Shared.Next(0, 100)))()), ct);
}

public static class DependencyRetry
{
    public static async Task ExecuteAsync(Func<CancellationToken, Task> operation, Func<CancellationToken, Task> exhausted, CancellationToken ct,
        PoliticaRetentativa? politica = null, Func<Exception, bool>? transient = null)
    {
        politica ??= PoliticaRetentativa.Padrao;
        for (var attempt = 1; ; attempt++)
        {
            try { await operation(ct); return; }
            catch (Exception ex) when (ex is not OperationCanceledException && (transient?.Invoke(ex) ?? true))
            {
                if (attempt >= politica.MaxTentativas) { await exhausted(ct); throw; }
                await politica.AguardarAsync(attempt, ct);
            }
        }
    }
}

public static class FalhaTransitoria
{
    public static bool EhTransitoria(Exception ex)
    {
        for (var atual = ex; atual is not null; atual = atual.InnerException)
            if (atual is NpgsqlException { IsTransient: true } or TimeoutException or IOException or System.Net.Sockets.SocketException) return true;
        return false;
    }
}

public enum ResultadoPublicacao { Enviado, EncaminhadoParaDlq }

public sealed class PublicadorSqsEventos(IAmazonSQS sqs, IConfiguration config, ILogger<PublicadorSqsEventos> logger, IAlertSink alerts, PoliticaRetentativa politica)
{
    public async Task<ResultadoPublicacao> PublicarAsync(MensagemSaida mensagem, CancellationToken ct)
    {
        var chave = mensagem.Destino switch
        {
            DestinosEvento.OsService => "Sqs:OsServiceQueueUrl",
            DestinosEvento.Billing => "Sqs:BillingQueueUrl",
            _ => throw new InvalidOperationException($"Destino de evento desconhecido: {mensagem.Destino}.")
        };
        var destino = config[chave] ?? throw new InvalidOperationException($"{chave} não configurada.");
        var dlq = config["Sqs:DeadLetterQueueUrl"] ?? throw new InvalidOperationException("Sqs:DeadLetterQueueUrl não configurada.");
        try
        {
            await DependencyRetry.ExecuteAsync(token => sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = destino, MessageBody = mensagem.Conteudo }, token),
                _ => Task.CompletedTask, ct, politica);
            return ResultadoPublicacao.Enviado;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try
            {
                await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = dlq, MessageBody = mensagem.Conteudo }, ct);
                await alerts.AlertarAsync("execucao.sqs.exhausted", $"{mensagem.Destino}/{mensagem.EventId}", ct);
                return ResultadoPublicacao.EncaminhadoParaDlq;
            }
            catch (Exception dlqError) when (dlqError is not OperationCanceledException)
            {
                logger.LogError(dlqError, "Falha ao encaminhar evento {EventId} do destino {Destino} à DLQ", mensagem.EventId, mensagem.Destino);
                await alerts.AlertarAsync("execucao.sqs.dlq_failed", $"{mensagem.Destino}/{mensagem.EventId}", ct);
                throw;
            }
        }
    }
}

// Lê o outbox em transação com FOR UPDATE SKIP LOCKED: várias instâncias não publicam a mesma linha.
public sealed class DespachanteOutbox(IServiceScopeFactory scopes, PublicadorSqsEventos publicador, ILogger<DespachanteOutbox> logger)
{
    public async Task<bool> DespacharProximaAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExecucaoDbContext>();
        await using var transacao = await db.Database.BeginTransactionAsync(ct);
        var mensagem = await db.MensagensSaida
            .FromSqlRaw("SELECT * FROM mensagens_saida WHERE \"EnviadoEm\" IS NULL AND (\"ProximaTentativaEm\" IS NULL OR \"ProximaTentativaEm\" <= now()) ORDER BY \"CriadoEm\", \"Id\" LIMIT 1 FOR UPDATE SKIP LOCKED")
            .FirstOrDefaultAsync(ct);
        if (mensagem is null) return false;
        try
        {
            var resultado = await publicador.PublicarAsync(mensagem, ct);
            mensagem.EnviadoEm = DateTimeOffset.UtcNow;
            mensagem.EncaminhadoParaDlq = resultado == ResultadoPublicacao.EncaminhadoParaDlq;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            mensagem.Tentativas++;
            mensagem.ProximaTentativaEm = DateTimeOffset.UtcNow.AddSeconds(Math.Pow(2, Math.Min(mensagem.Tentativas, 6)) + Random.Shared.NextDouble());
            logger.LogError(ex, "Falha ao publicar evento {EventId}; permanece no outbox (tentativa {Tentativas}).", mensagem.EventId, mensagem.Tentativas);
        }
        await db.SaveChangesAsync(ct);
        await transacao.CommitAsync(ct);
        return true;
    }
}

// Consome ordem-servico.liberada.v1. Entrega pelo menos uma vez: a deduplicação persistida está em ExecucaoService.
public sealed class ConsumidorOsLiberada(IServiceScopeFactory scopes, IAmazonSQS sqs, IConfiguration config, ILogger<ConsumidorOsLiberada> logger, IAlertSink alerts, PoliticaRetentativa politica)
{
    public async Task<int> ReceberLoteAsync(CancellationToken ct)
    {
        var fila = config["Sqs:LiberadasQueueUrl"] ?? throw new InvalidOperationException("Sqs:LiberadasQueueUrl não configurada.");
        var resposta = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = fila, MaxNumberOfMessages = 10, WaitTimeSeconds = 20 }, ct);
        foreach (var mensagem in resposta.Messages ?? []) await ProcessarAsync(mensagem, ct);
        return resposta.Messages?.Count ?? 0;
    }

    public async Task ProcessarAsync(Message mensagem, CancellationToken ct)
    {
        var fila = config["Sqs:LiberadasQueueUrl"] ?? throw new InvalidOperationException("Sqs:LiberadasQueueUrl não configurada.");
        if (!EventoOsLiberada.TentarLer(mensagem.Body, out var evento))
        {
            await EncaminharParaDlqAsync(mensagem, "execucao.evento_invalido", mensagem.MessageId ?? "sem-id", ct);
            await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = fila, ReceiptHandle = mensagem.ReceiptHandle }, ct);
            return;
        }
        try
        {
            await DependencyRetry.ExecuteAsync(async token =>
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ExecucaoService>().ReceberOsLiberadaAsync(evento!, token);
            }, token => alerts.AlertarAsync("execucao.banco.exhausted", $"Tentativas esgotadas ao registrar o evento {evento!.EventId}.", token), ct, politica, FalhaTransitoria.EhTransitoria);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && FalhaTransitoria.EhTransitoria(ex))
        {
            logger.LogError(ex, "Evento {EventId} encaminhado à DLQ após esgotar as tentativas no banco.", evento!.EventId);
            await EncaminharParaDlqAsync(mensagem, null, evento.EventId.ToString(), ct);
        }
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = fila, ReceiptHandle = mensagem.ReceiptHandle }, ct);
    }

    private async Task EncaminharParaDlqAsync(Message mensagem, string? alerta, string referencia, CancellationToken ct)
    {
        var dlq = config["Sqs:LiberadasDeadLetterQueueUrl"] ?? throw new InvalidOperationException("Sqs:LiberadasDeadLetterQueueUrl não configurada.");
        try
        {
            await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = dlq, MessageBody = mensagem.Body }, ct);
            if (alerta is not null) await alerts.AlertarAsync(alerta, referencia, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Falha ao encaminhar a mensagem {Referencia} à DLQ de entrada; ela volta à fila após o timeout de visibilidade.", referencia);
            await alerts.AlertarAsync("execucao.entrada.dlq_failed", referencia, ct);
            throw;
        }
    }
}
