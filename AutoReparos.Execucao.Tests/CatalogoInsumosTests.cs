using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AutoReparos.Execucao.Domain;
using AutoReparos.Execucao.Tests.Suporte;
using Microsoft.EntityFrameworkCore;

namespace AutoReparos.Execucao.Tests;

// Catálogo de serviços, insumos com saldos, entrada de estoque e bloqueio de execução cancelada.
[Collection("postgres")]
public sealed class CatalogoInsumosTests(PostgresFixture postgres)
{
    [Fact]
    public async Task CatalogoExpoePrecoESaldos()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var oleo = await ambiente.Fabrica.SemearInsumoAsync("Óleo 5W30", 10, 42.50m);
        await ambiente.Consumidor.ProcessarAsync(Mensagens.ReservarInsumos(Guid.NewGuid(), Guid.NewGuid(), [(oleo.Id, 4)]), CancellationToken.None);
        using var admin = ambiente.Fabrica.Cliente("Administrador", "adm-1");
        using var mecanico = ambiente.Fabrica.Cliente("Mecanico");
        using var atendente = ambiente.Fabrica.Cliente("Atendente");
        using var anonimo = ambiente.Fabrica.Cliente(null);

        // GET insumos: 200 com preço e os dois saldos; 401 sem autenticação.
        var insumos = await mecanico.GetAsync("/api/v1/insumos?nome=%C3%B3leo");
        Assert.Equal(HttpStatusCode.OK, insumos.StatusCode);
        var item = Assert.Single((await insumos.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("itens").EnumerateArray(), i => i.GetProperty("id").GetGuid() == oleo.Id);
        Assert.Equal((42.50m, 6, 4), (item.GetProperty("precoUnitario").GetDecimal(), item.GetProperty("quantidadeDisponivel").GetInt32(), item.GetProperty("quantidadeReservada").GetInt32()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync("/api/v1/insumos")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await mecanico.GetAsync("/api/v1/insumos?nome=%20")).StatusCode);

        // POST insumos: 201 só para Administrador, com saldo inicial; 401, 403 e 422 nos demais casos.
        var criado = await admin.PostAsJsonAsync("/api/v1/insumos", new { nome = "Filtro de ar", descricao = "Filtro universal", precoUnitario = 35.90m, quantidadeInicial = 12 });
        Assert.Equal(HttpStatusCode.Created, criado.StatusCode);
        var corpoCriado = await criado.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((35.90m, 12, 0), (corpoCriado.GetProperty("precoUnitario").GetDecimal(), corpoCriado.GetProperty("quantidadeDisponivel").GetInt32(), corpoCriado.GetProperty("quantidadeReservada").GetInt32()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.PostAsJsonAsync("/api/v1/insumos", new { nome = "X", precoUnitario = 1m })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await atendente.PostAsJsonAsync("/api/v1/insumos", new { nome = "X", precoUnitario = 1m })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await mecanico.PostAsJsonAsync("/api/v1/insumos", new { nome = "X", precoUnitario = 1m })).StatusCode);
        foreach (var invalido in new object[]
        {
            new { precoUnitario = 1m }, new { nome = " ", precoUnitario = 1m }, new { nome = "X" }, new { nome = "X", precoUnitario = 0m },
            new { nome = "X", precoUnitario = -3m }, new { nome = "X", precoUnitario = 1.005m }, new { nome = "X", precoUnitario = 1m, quantidadeInicial = -1 },
            new { nome = "X", precoUnitario = 1m, quantidadeInicial = 1.5m }, new { nome = new string('a', 101), precoUnitario = 1m }
        })
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsJsonAsync("/api/v1/insumos", invalido)).StatusCode);

        // Serviços: mesma matriz.
        var servico = await admin.PostAsJsonAsync("/api/v1/servicos", new { nome = "Alinhamento", descricao = "Alinhamento e balanceamento", precoUnitario = 160m });
        Assert.Equal(HttpStatusCode.Created, servico.StatusCode);
        var lista = await atendente.GetAsync("/api/v1/servicos?nome=alinha");
        Assert.Equal(HttpStatusCode.OK, lista.StatusCode);
        var servicoListado = Assert.Single((await lista.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("itens").EnumerateArray(), s => s.GetProperty("nome").GetString() == "Alinhamento");
        Assert.Equal(160m, servicoListado.GetProperty("precoUnitario").GetDecimal());
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync("/api/v1/servicos")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.PostAsJsonAsync("/api/v1/servicos", new { nome = "X", precoUnitario = 1m })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await mecanico.PostAsJsonAsync("/api/v1/servicos", new { nome = "X", precoUnitario = 1m })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsJsonAsync("/api/v1/servicos", new { nome = "X", precoUnitario = 0m })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsJsonAsync("/api/v1/servicos", new { precoUnitario = 10m })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsync("/api/v1/servicos", Mensagens.Json("[1]"))).StatusCode);
    }

    [Fact]
    public async Task SeedDaMigrationTrazCatalogoInicial()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        using var mecanico = ambiente.Fabrica.Cliente("Mecanico");

        var servicos = (await (await mecanico.GetAsync("/api/v1/servicos")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("itens");
        var insumos = (await (await mecanico.GetAsync("/api/v1/insumos")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("itens");

        Assert.Equal(5, servicos.GetArrayLength());
        Assert.Equal(5, insumos.GetArrayLength());
        Assert.All(insumos.EnumerateArray(), i => Assert.Equal(0, i.GetProperty("quantidadeReservada").GetInt32()));
    }

    [Fact]
    public async Task EntradaAumentaSaldoEMovimentacao()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var oleo = await ambiente.Fabrica.SemearInsumoAsync("Óleo", 10);
        string Rota(Guid id) => $"/api/v1/insumos/{id}/entradas";
        using var admin = ambiente.Fabrica.Cliente("Administrador", "adm-1");
        using var atendente = ambiente.Fabrica.Cliente("Atendente", "atd-1");
        using var mecanico = ambiente.Fabrica.Cliente("Mecanico");
        using var anonimo = ambiente.Fabrica.Cliente(null);

        var resposta = await admin.PostAsJsonAsync(Rota(oleo.Id), new { quantidade = 5 });
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal(15, (await resposta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("quantidadeDisponivel").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await atendente.PostAsJsonAsync(Rota(oleo.Id), new { quantidade = 3 })).StatusCode);

        Assert.Equal(18, (await ambiente.Fabrica.InsumoAsync(oleo.Id)).QuantidadeDisponivel);
        var movimentacoes = (await ambiente.Fabrica.NoBancoAsync(db => db.MovimentacoesInsumo.AsNoTracking().ToListAsync())).OrderBy(m => m.Quantidade).ToList();
        Assert.Equal([(3, "atd-1"), (5, "adm-1")], movimentacoes.Select(m => (m.Quantidade, m.Responsavel)));
        Assert.All(movimentacoes, m => Assert.Equal(oleo.Id, m.InsumoId));

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.PostAsJsonAsync(Rota(oleo.Id), new { quantidade = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await mecanico.PostAsJsonAsync(Rota(oleo.Id), new { quantidade = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync(Rota(Guid.NewGuid()), new { quantidade = 1 })).StatusCode);
        foreach (var invalido in new object[] { new { quantidade = 0 }, new { quantidade = -2 }, new { quantidade = 1.5m }, new { quantidade = "3" }, new { outro = 1 }, new { quantidade = 1_000_001 } })
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsJsonAsync(Rota(oleo.Id), invalido)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsync(Rota(oleo.Id), Mensagens.Json("{"))).StatusCode);

        // Saldo acima do teto é recusado sem alterar nada (nem a movimentação).
        var cheio = await ambiente.Fabrica.SemearInsumoAsync("Cheio", 1);
        await ambiente.Fabrica.NoBancoAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE insumos SET \"QuantidadeDisponivel\" = {LimitesCatalogo.SaldoMaximo - 1} WHERE \"Id\" = {cheio.Id}"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsJsonAsync(Rota(cheio.Id), new { quantidade = 2 })).StatusCode);
        Assert.Equal(LimitesCatalogo.SaldoMaximo - 1, (await ambiente.Fabrica.InsumoAsync(cheio.Id)).QuantidadeDisponivel);
        Assert.Equal(2, await ambiente.Fabrica.NoBancoAsync(db => db.MovimentacoesInsumo.CountAsync()));
    }

    [Fact]
    public async Task EntradasConcorrentesSomamTodas()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var oleo = await ambiente.Fabrica.SemearInsumoAsync("Óleo", 0);
        using var admin = ambiente.Fabrica.Cliente("Administrador", "adm-1");

        var respostas = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => admin.PostAsJsonAsync($"/api/v1/insumos/{oleo.Id}/entradas", new { quantidade = 2 })));

        Assert.All(respostas, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(20, (await ambiente.Fabrica.InsumoAsync(oleo.Id)).QuantidadeDisponivel);
    }

    [Fact]
    public async Task ExecucaoCanceladaRecusa409()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var ordem = await ambiente.Fabrica.SemearAsync(o => o.RegistrarEtapa(TipoEtapa.Reparo, "Reparo", "mec-1", DateTimeOffset.UtcNow));
        await ambiente.Consumidor.ProcessarAsync(Mensagens.Cancelar(Guid.NewGuid(), ordem.OrdemServicoId), CancellationToken.None);
        using var mecanico = ambiente.Fabrica.Cliente("Mecanico", "mec-1");

        var etapa = await mecanico.PostAsJsonAsync($"/api/v1/execucoes/{ordem.Id}/etapas", new { tipo = "Reparo", descricao = "Nova etapa" });
        var conclusao = await mecanico.PostAsync($"/api/v1/execucoes/{ordem.Id}/conclusao", null);

        Assert.Equal((HttpStatusCode.Conflict, HttpStatusCode.Conflict), (etapa.StatusCode, conclusao.StatusCode));
        Assert.Equal("execucao_estado_invalido", (await conclusao.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codigo").GetString());
        Assert.Equal(EstadoExecucao.Cancelada, (await ambiente.Fabrica.NoBancoAsync(db => db.Ordens.AsNoTracking().SingleAsync())).Estado);
        Assert.Equal(1, (await ambiente.Fabrica.NoBancoAsync(db => db.Etapas.CountAsync())));
    }
}
