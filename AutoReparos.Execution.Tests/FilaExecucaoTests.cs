using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AutoReparos.Execution.Domain;
using AutoReparos.Execution.Tests.Suporte;
using Microsoft.EntityFrameworkCore;

namespace AutoReparos.Execution.Tests;

[Collection("postgres")]
public sealed class FilaExecucaoTests(PostgresFixture postgres)
{
    private const string Rota = "/api/v1/fila-execucao";

    [Fact]
    public async Task ReleasedOrderQueuedOnce_ConsumerInsertsOrderAndQueueExposesIt()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        Guid eventId = Guid.NewGuid(), os = Guid.NewGuid(), cliente = Guid.NewGuid(), veiculo = Guid.NewGuid();
        var mensagem = Mensagens.Liberada(eventId, os, cliente, veiculo);

        await ambiente.Consumidor.ProcessarAsync(mensagem, CancellationToken.None);

        using var http = ambiente.Fabrica.Cliente("Mecanico");
        var resposta = await http.GetAsync(Rota);
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var corpo = await resposta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, corpo.GetProperty("total").GetInt32());
        var item = Assert.Single(corpo.GetProperty("itens").EnumerateArray().ToList());
        Assert.Equal(os, item.GetProperty("ordemServicoId").GetGuid());
        Assert.Equal(cliente, item.GetProperty("clienteId").GetGuid());
        Assert.Equal(veiculo, item.GetProperty("veiculoId").GetGuid());
        Assert.Equal("Fila", item.GetProperty("status").GetString());
        Assert.False(item.GetProperty("intervencaoRequerida").GetBoolean());
        Assert.Equal(0, item.GetProperty("etapas").GetArrayLength());

        Assert.Equal(1, await ambiente.Fabrica.NoBancoAsync(db => db.Ordens.CountAsync()));
        Assert.Equal([eventId], await ambiente.Fabrica.NoBancoAsync(db => db.EventosProcessados.Select(e => e.EventId).ToListAsync()));
        var removida = Assert.Single(ambiente.Sqs.Removidas);
        Assert.Equal((FabricaApi.FilaLiberadas, mensagem.ReceiptHandle), removida);
        Assert.Empty(ambiente.Sqs.Enviadas);
        Assert.Empty(ambiente.Alertas.Itens);
    }

    [Fact]
    public async Task ReleasedOrderQueuedOnce_SameOrderWithAnotherEventIdKeepsSingleQueueEntry()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var os = Guid.NewGuid();
        await ambiente.Consumidor.ProcessarAsync(Mensagens.Liberada(Guid.NewGuid(), os), CancellationToken.None);
        await ambiente.Consumidor.ProcessarAsync(Mensagens.Liberada(Guid.NewGuid(), os), CancellationToken.None);

        Assert.Equal(1, await ambiente.Fabrica.NoBancoAsync(db => db.Ordens.CountAsync(o => o.OrdemServicoId == os)));
        Assert.Equal(2, await ambiente.Fabrica.NoBancoAsync(db => db.EventosProcessados.CountAsync()));
        Assert.Equal(2, ambiente.Sqs.Removidas.Count);
    }

    [Fact]
    public async Task ReleasedOrderQueuedOnce_ConcurrentEventsForSameOrderCreateOneEntry()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var os = Guid.NewGuid();
        ambiente.Gate.Armar(2);
        await Task.WhenAll(
            ambiente.Consumidor.ProcessarAsync(Mensagens.Liberada(Guid.NewGuid(), os), CancellationToken.None),
            ambiente.Consumidor.ProcessarAsync(Mensagens.Liberada(Guid.NewGuid(), os), CancellationToken.None));
        ambiente.Gate.Armar(0);

        Assert.Equal(1, await ambiente.Fabrica.NoBancoAsync(db => db.Ordens.CountAsync(o => o.OrdemServicoId == os)));
        Assert.Equal(2, ambiente.Sqs.Removidas.Count);
        Assert.Empty(ambiente.Sqs.Enviadas);
        Assert.Empty(ambiente.Alertas.Itens);
    }

    [Fact]
    public async Task ReleasedOrderQueuedOnce_QueueRequiresMechanicOrAdministrator()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        using var anonimo = ambiente.Fabrica.Cliente(null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync(Rota)).StatusCode);
        foreach (var papel in new[] { "Atendente", "Cliente", "ServicoOS" })
        {
            using var http = ambiente.Fabrica.Cliente(papel);
            Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync(Rota)).StatusCode);
        }
        foreach (var papel in new[] { "Mecanico", "Administrador" })
        {
            using var http = ambiente.Fabrica.Cliente(papel);
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(Rota)).StatusCode);
        }
    }

    [Fact]
    public async Task ReleasedOrderQueuedOnce_StatusAndMechanicFilters()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var naFila = await ambiente.Fabrica.SemearAsync();
        var emExecucao = await ambiente.Fabrica.SemearAsync(o => o.RegistrarEtapa(TipoEtapa.Diagnostico, "Checar freios", "mec-1", DateTimeOffset.UtcNow));
        var concluida = await ambiente.Fabrica.SemearAsync(o => { o.RegistrarEtapa(TipoEtapa.Reparo, "Trocar pastilhas", "mec-2", DateTimeOffset.UtcNow); o.Concluir(DateTimeOffset.UtcNow); });
        var falhou = await ambiente.Fabrica.SemearAsync(o => { o.RegistrarEtapa(TipoEtapa.Reparo, "Trocar disco", "mec-1", DateTimeOffset.UtcNow); o.Falhar("Peça errada", DateTimeOffset.UtcNow); });
        using var http = ambiente.Fabrica.Cliente("Administrador");

        async Task<List<Guid>> Ids(string consulta)
        {
            var resposta = await http.GetAsync(Rota + consulta);
            Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
            var corpo = await resposta.Content.ReadFromJsonAsync<JsonElement>();
            return corpo.GetProperty("itens").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
        }

        // Sem filtro: tudo que ainda exige ação (a concluída fica de fora).
        Assert.Equal([naFila.Id, emExecucao.Id, falhou.Id], await Ids(""));
        Assert.Equal([naFila.Id], await Ids("?status=Fila"));
        Assert.Equal([concluida.Id], await Ids("?status=concluida"));
        Assert.Equal([emExecucao.Id, falhou.Id], await Ids("?status=EmExecucao,Falhou"));
        Assert.Equal([naFila.Id, concluida.Id], await Ids("?status=Fila&status=Concluida"));
        Assert.Equal([emExecucao.Id, falhou.Id], await Ids("?mecanico=mec-1"));
        Assert.Equal([falhou.Id], await Ids("?mecanico=mec-1&status=Falhou"));
        Assert.Equal([concluida.Id], await Ids("?mecanico=mec-2&status=Concluida"));
        Assert.Empty(await Ids("?mecanico=ninguem"));
    }

    [Theory]
    [InlineData("?status=Inexistente")]
    [InlineData("?status=1")]
    [InlineData("?status=Fila,,Falhou")]
    [InlineData("?mecanico=%20")]
    public async Task ReleasedOrderQueuedOnce_InvalidFilterReturns422(string consulta)
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        using var http = ambiente.Fabrica.Cliente("Mecanico");
        var resposta = await http.GetAsync(Rota + consulta);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resposta.StatusCode);
        Assert.Equal("filtro_invalido", (await resposta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codigo").GetString());
    }
}
