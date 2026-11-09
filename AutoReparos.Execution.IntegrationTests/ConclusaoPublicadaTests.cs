using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AutoReparos.Execution.Application;
using AutoReparos.Execution.Domain;
using AutoReparos.Execution.Tests.Suporte;
using Microsoft.EntityFrameworkCore;

namespace AutoReparos.Execution.IntegrationTests;

// Sem AWS e sem variáveis de ambiente: PostgreSQL real via Testcontainers e um IAmazonSQS falso que captura os envios.
[Collection("postgres")]
public sealed class ConclusaoPublicadaTests(PostgresFixture postgres)
{
    private static async Task<(Guid ExecucaoId, EventoOsLiberada Evento)> LiberarEConcluirAsync(Ambiente ambiente)
    {
        Guid eventId = Guid.NewGuid(), os = Guid.NewGuid(), cliente = Guid.NewGuid(), veiculo = Guid.NewGuid();
        await ambiente.Consumidor.ProcessarAsync(Mensagens.Liberada(eventId, os, cliente, veiculo), CancellationToken.None);
        var execucaoId = await ambiente.Fabrica.NoBancoAsync(db => db.Ordens.Select(o => o.Id).SingleAsync());
        using var mecanico = ambiente.Fabrica.Cliente("Mecanico", "mec-1");
        Assert.Equal(HttpStatusCode.Created, (await mecanico.PostAsJsonAsync($"/api/v1/execucoes/{execucaoId}/etapas", new { tipo = "Reparo", descricao = "Troca do alternador" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await mecanico.PostAsync($"/api/v1/execucoes/{execucaoId}/conclusao", null)).StatusCode);
        return (execucaoId, new EventoOsLiberada(eventId, os, cliente, veiculo));
    }

    private static void AssertEventoConcluido(string corpo, Guid execucaoId, EventoOsLiberada origem, DateTimeOffset concluidaEm)
    {
        var evento = JsonDocument.Parse(corpo).RootElement;
        Assert.NotEqual(Guid.Empty, evento.GetProperty("eventId").GetGuid());
        Assert.Equal("execucao.concluida.v1", evento.GetProperty("tipo").GetString());
        Assert.Equal(execucaoId, evento.GetProperty("id").GetGuid());
        Assert.Equal(origem.OrdemServicoId, evento.GetProperty("ordemServicoId").GetGuid());
        Assert.Equal(origem.ClienteId, evento.GetProperty("clienteId").GetGuid());
        Assert.Equal(origem.VeiculoId, evento.GetProperty("veiculoId").GetGuid());
        Assert.Equal(concluidaEm, evento.GetProperty("concluidaEm").GetDateTimeOffset());
    }

    [Fact]
    public async Task CompletionPublishedToConsumers_OsServiceAndBillingQueuesReceiveTheEvent()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var concluidaEm = new DateTimeOffset(2026, 11, 13, 18, 45, 0, TimeSpan.Zero);
        ambiente.Relogio.Agora = concluidaEm;
        var (execucaoId, origem) = await LiberarEConcluirAsync(ambiente);
        // O outbox ainda não foi despachado: nada foi enviado ao SQS.
        Assert.Empty(ambiente.Sqs.Enviadas);

        var despachadas = 0;
        while (await ambiente.Despachante.DespacharProximaAsync(CancellationToken.None)) despachadas++;

        Assert.Equal(2, despachadas);
        var enviadas = ambiente.Sqs.Enviadas;
        Assert.Equal([FabricaApi.FilaBilling, FabricaApi.FilaOs], enviadas.Select(e => e.Fila).Order().ToArray());
        var paraOs = enviadas.Single(e => e.Fila == FabricaApi.FilaOs).Corpo;
        var paraBilling = enviadas.Single(e => e.Fila == FabricaApi.FilaBilling).Corpo;
        AssertEventoConcluido(paraOs, execucaoId, origem, concluidaEm);
        AssertEventoConcluido(paraBilling, execucaoId, origem, concluidaEm);
        // Cada consumidor recebe o mesmo evento (mesmo eventId) para deduplicar do seu lado.
        Assert.Equal(JsonDocument.Parse(paraOs).RootElement.GetProperty("eventId").GetGuid(), JsonDocument.Parse(paraBilling).RootElement.GetProperty("eventId").GetGuid());

        var saida = await ambiente.Fabrica.NoBancoAsync(db => db.MensagensSaida.AsNoTracking().ToListAsync());
        Assert.All(saida, m => { Assert.NotNull(m.EnviadoEm); Assert.False(m.EncaminhadoParaDlq); Assert.Equal(0, m.Tentativas); });
        Assert.Empty(ambiente.Alertas.Itens);
        Assert.DoesNotContain(enviadas, e => e.Fila == FabricaApi.DlqSaida);

        // Um novo ciclo do publicador não reenvia nada.
        Assert.False(await ambiente.Despachante.DespacharProximaAsync(CancellationToken.None));
        Assert.Equal(2, ambiente.Sqs.Enviadas.Count);
    }

    [Fact]
    public async Task CompletionPublishedToConsumers_BackgroundServicesCarryReleasedOrderToBothQueues()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres, segundoPlano: true);
        Guid eventId = Guid.NewGuid(), os = Guid.NewGuid(), cliente = Guid.NewGuid(), veiculo = Guid.NewGuid();
        var mensagem = Mensagens.Liberada(eventId, os, cliente, veiculo);
        ambiente.Sqs.ParaReceber.Enqueue(mensagem);
        ambiente.Fabrica.CreateClient().Dispose(); // garante o host (e os serviços em segundo plano) iniciado
        using var mecanico = ambiente.Fabrica.Cliente("Mecanico", "mec-1");

        // O consumidor em segundo plano coloca a OS na fila de execução e apaga a mensagem.
        Guid execucaoId = Guid.Empty;
        await EsperarAsync(async () =>
        {
            var fila = await (await mecanico.GetAsync("/api/v1/fila-execucao")).Content.ReadFromJsonAsync<JsonElement>();
            if (fila.GetProperty("total").GetInt32() != 1) return false;
            execucaoId = fila.GetProperty("itens")[0].GetProperty("id").GetGuid();
            return true;
        });
        Assert.Equal([(FabricaApi.FilaLiberadas, mensagem.ReceiptHandle)], ambiente.Sqs.Removidas);

        Assert.Equal(HttpStatusCode.Created, (await mecanico.PostAsJsonAsync($"/api/v1/execucoes/{execucaoId}/etapas", new { tipo = "Diagnostico", descricao = "Teste de bateria" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await mecanico.PostAsync($"/api/v1/execucoes/{execucaoId}/conclusao", null)).StatusCode);

        // O publicador em segundo plano esvazia o outbox.
        await EsperarAsync(() => Task.FromResult(ambiente.Sqs.Enviadas.Count == 2));
        Assert.Equal([FabricaApi.FilaBilling, FabricaApi.FilaOs], ambiente.Sqs.Enviadas.Select(e => e.Fila).Order().ToArray());
        foreach (var (_, corpo) in ambiente.Sqs.Enviadas)
        {
            var evento = JsonDocument.Parse(corpo).RootElement;
            Assert.Equal(("execucao.concluida.v1", os), (evento.GetProperty("tipo").GetString(), evento.GetProperty("ordemServicoId").GetGuid()));
        }
        Assert.Empty(ambiente.Alertas.Itens);
    }

    private static async Task EsperarAsync(Func<Task<bool>> condicao)
    {
        var limite = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < limite)
        {
            if (await condicao()) return;
            await Task.Delay(100);
        }
        Assert.Fail("A condição não foi atendida em 30 segundos.");
    }
}
