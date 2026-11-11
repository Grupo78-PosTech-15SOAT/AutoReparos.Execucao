using AutoReparos.Execucao.Infrastructure;

namespace AutoReparos.Execucao.Api;

public sealed class PublicadorOutboxServico(DespachanteOutbox despachante, IConfiguration configuration, ILogger<PublicadorOutboxServico> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("BackgroundServices:Enabled", true)) return;
        var falhas = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processou = await despachante.DespacharProximaAsync(stoppingToken);
                falhas = 0;
                if (!processou) await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                falhas = Math.Min(falhas + 1, 6);
                logger.LogError(ex, "Falha ao despachar o outbox; os eventos permanecem pendentes.");
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, falhas) + Random.Shared.NextDouble()), stoppingToken);
            }
        }
    }
}

public sealed class ConsumidorOsLiberadaServico(ConsumidorOsLiberada consumidor, IConfiguration configuration, ILogger<ConsumidorOsLiberadaServico> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("BackgroundServices:Enabled", true)) return;
        if (string.IsNullOrWhiteSpace(configuration["Sqs:LiberadasQueueUrl"]))
        {
            logger.LogError("Sqs:LiberadasQueueUrl não configurada; o consumidor de OS liberadas não será iniciado.");
            return;
        }
        var falhas = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await consumidor.ReceberLoteAsync(stoppingToken); falhas = 0; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                falhas = Math.Min(falhas + 1, 6);
                logger.LogError(ex, "Falha ao consumir OS liberadas; a mensagem volta à fila após o timeout de visibilidade.");
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, falhas) + Random.Shared.NextDouble()), stoppingToken);
            }
        }
    }
}
