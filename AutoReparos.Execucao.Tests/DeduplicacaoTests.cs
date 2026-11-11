using System.Net;
using System.Net.Http.Json;
using AutoReparos.Execucao.Domain;
using AutoReparos.Execucao.Tests.Suporte;
using Microsoft.EntityFrameworkCore;

namespace AutoReparos.Execucao.Tests;

[Collection("postgres")]
public sealed class DeduplicacaoTests(PostgresFixture postgres)
{
    [Fact]
    public async Task DuplicateEventDeduplication_RepeatedEventDoesNotCreateSecondQueueItem()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        Guid eventId = Guid.NewGuid(), os = Guid.NewGuid();
        var mensagem = Mensagens.Liberada(eventId, os);

        for (var i = 0; i < 3; i++) await ambiente.Consumidor.ProcessarAsync(mensagem, CancellationToken.None);

        Assert.Equal(1, await ambiente.Fabrica.NoBancoAsync(db => db.Ordens.CountAsync()));
        Assert.Equal(1, await ambiente.Fabrica.NoBancoAsync(db => db.EventosProcessados.CountAsync()));
        Assert.Equal(3, ambiente.Sqs.Removidas.Count);
        Assert.Empty(ambiente.Sqs.Enviadas);
        Assert.Empty(ambiente.Alertas.Itens);
    }

    [Fact]
    public async Task DuplicateEventDeduplication_RepeatedEventAfterProgressKeepsStagesAndState()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        Guid eventId = Guid.NewGuid(), os = Guid.NewGuid();
        var mensagem = Mensagens.Liberada(eventId, os);
        await ambiente.Consumidor.ProcessarAsync(mensagem, CancellationToken.None);
        var execucaoId = await ambiente.Fabrica.NoBancoAsync(db => db.Ordens.Select(o => o.Id).SingleAsync());
        using var http = ambiente.Fabrica.Cliente("Mecanico", "mec-1");
        Assert.Equal(HttpStatusCode.Created, (await http.PostAsJsonAsync($"/api/v1/execucoes/{execucaoId}/etapas", new { tipo = "Diagnostico", descricao = "Checar suspensão" })).StatusCode);

        await ambiente.Consumidor.ProcessarAsync(mensagem, CancellationToken.None);

        var ordem = await ambiente.Fabrica.NoBancoAsync(db => db.Ordens.Include(o => o.Etapas).SingleAsync());
        Assert.Equal(EstadoExecucao.EmExecucao, ordem.Estado);
        Assert.Single(ordem.Etapas);
        Assert.Equal("Checar suspensão", ordem.Etapas[0].Descricao);
        Assert.Equal(1, await ambiente.Fabrica.NoBancoAsync(db => db.EventosProcessados.CountAsync()));
    }

    [Fact]
    public async Task DuplicateEventDeduplication_ConcurrentDeliveriesOfSameEventCreateOneEntry()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        Guid eventId = Guid.NewGuid(), os = Guid.NewGuid();
        var mensagem = Mensagens.Liberada(eventId, os);
        ambiente.Gate.Armar(2);

        await Task.WhenAll(ambiente.Consumidor.ProcessarAsync(mensagem, CancellationToken.None), ambiente.Consumidor.ProcessarAsync(mensagem, CancellationToken.None));
        ambiente.Gate.Armar(0);

        Assert.Equal(1, await ambiente.Fabrica.NoBancoAsync(db => db.Ordens.CountAsync()));
        Assert.Equal(1, await ambiente.Fabrica.NoBancoAsync(db => db.EventosProcessados.CountAsync()));
        Assert.Equal(2, ambiente.Sqs.Removidas.Count);
        Assert.Empty(ambiente.Sqs.Enviadas);
        Assert.Empty(ambiente.Alertas.Itens);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("")]
    [InlineData("{\"eventId\":\"6f1c2a3e-0b7d-4c55-9a1e-2d8f4b6c7a90\",\"tipo\":\"ordem-servico.criada.v1\",\"id\":\"b3d5f7a1-1c2e-4f60-8a9b-0c1d2e3f4a5b\",\"clienteId\":\"0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d\",\"veiculoId\":\"5e6f7a8b-9c0d-4e1f-a2b3-c4d5e6f7a8b9\"}")]
    [InlineData("{\"eventId\":\"{6f1c2a3e-0b7d-4c55-9a1e-2d8f4b6c7a90}\",\"tipo\":\"ordem-servico.liberada.v1\",\"id\":\"b3d5f7a1-1c2e-4f60-8a9b-0c1d2e3f4a5b\",\"clienteId\":\"0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d\",\"veiculoId\":\"5e6f7a8b-9c0d-4e1f-a2b3-c4d5e6f7a8b9\"}")]
    [InlineData("{\"eventId\":\"6f1c2a3e0b7d4c559a1e2d8f4b6c7a90\",\"tipo\":\"ordem-servico.liberada.v1\",\"id\":\"b3d5f7a1-1c2e-4f60-8a9b-0c1d2e3f4a5b\",\"clienteId\":\"0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d\",\"veiculoId\":\"5e6f7a8b-9c0d-4e1f-a2b3-c4d5e6f7a8b9\"}")]
    [InlineData("{\"eventId\":\"00000000-0000-0000-0000-000000000000\",\"tipo\":\"ordem-servico.liberada.v1\",\"id\":\"b3d5f7a1-1c2e-4f60-8a9b-0c1d2e3f4a5b\",\"clienteId\":\"0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d\",\"veiculoId\":\"5e6f7a8b-9c0d-4e1f-a2b3-c4d5e6f7a8b9\"}")]
    [InlineData("{\"eventId\":\"6f1c2a3e-0b7d-4c55-9a1e-2d8f4b6c7a90\",\"tipo\":\"ordem-servico.liberada.v1\",\"id\":123,\"clienteId\":\"0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d\",\"veiculoId\":\"5e6f7a8b-9c0d-4e1f-a2b3-c4d5e6f7a8b9\"}")]
    [InlineData("{\"eventId\":\"6f1c2a3e-0b7d-4c55-9a1e-2d8f4b6c7a90\",\"tipo\":\"\\ud800\",\"id\":\"b3d5f7a1-1c2e-4f60-8a9b-0c1d2e3f4a5b\",\"clienteId\":\"0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d\",\"veiculoId\":\"5e6f7a8b-9c0d-4e1f-a2b3-c4d5e6f7a8b9\"}")]
    [InlineData("{\"eventId\":\"6f1c2a3e-0b7d-4c55-9a1e-2d8f4b6c7a90\",\"tipo\":\"ordem-servico.liberada.v1\",\"id\":\"b3d5f7a1-1c2e-4f60-8a9b-0c1d2e3f4a5b\"}")]
    public async Task DuplicateEventDeduplication_MalformedEventGoesToDeadLetterWithoutPersisting(string corpo)
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var mensagem = new Amazon.SQS.Model.Message { MessageId = "m-1", ReceiptHandle = "r-1", Body = corpo };

        await ambiente.Consumidor.ProcessarAsync(mensagem, CancellationToken.None);

        Assert.Equal(0, await ambiente.Fabrica.NoBancoAsync(db => db.Ordens.CountAsync()));
        Assert.Equal(0, await ambiente.Fabrica.NoBancoAsync(db => db.EventosProcessados.CountAsync()));
        Assert.Equal([(FabricaApi.DlqLiberadas, corpo)], ambiente.Sqs.Enviadas);
        Assert.Equal([("execucao.evento_invalido", "m-1")], ambiente.Alertas.Itens);
        Assert.Equal([(FabricaApi.FilaLiberadas, "r-1")], ambiente.Sqs.Removidas);
    }
}
