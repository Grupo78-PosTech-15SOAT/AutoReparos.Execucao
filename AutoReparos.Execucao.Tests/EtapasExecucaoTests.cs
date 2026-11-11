using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AutoReparos.Execucao.Domain;
using AutoReparos.Execucao.Tests.Suporte;
using Microsoft.EntityFrameworkCore;

namespace AutoReparos.Execucao.Tests;

[Collection("postgres")]
public sealed class EtapasExecucaoTests(PostgresFixture postgres)
{
    private static string Rota(Guid id) => $"/api/v1/execucoes/{id}/etapas";

    [Fact]
    public async Task ExecutionStageHistory_PersistsTypeDescriptionResponsibleAndTimestamp()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var ordem = await ambiente.Fabrica.SemearAsync();
        var primeiro = new DateTimeOffset(2026, 11, 13, 13, 15, 0, TimeSpan.Zero);
        var segundo = primeiro.AddHours(2);

        ambiente.Relogio.Agora = primeiro;
        using var mecanico = ambiente.Fabrica.Cliente("Mecanico", "mec-1");
        var criada = await mecanico.PostAsJsonAsync(Rota(ordem.Id), new { tipo = "Diagnostico", descricao = "Verificar o sistema de freios" });
        Assert.Equal(HttpStatusCode.Created, criada.StatusCode);
        var corpo = await criada.Content.ReadFromJsonAsync<JsonElement>();
        var etapaId = corpo.GetProperty("id").GetGuid();
        Assert.Equal($"/api/v1/execucoes/{ordem.Id}/etapas/{etapaId}", criada.Headers.Location!.ToString());
        Assert.Equal(1, corpo.GetProperty("sequencia").GetInt32());
        Assert.Equal("Diagnostico", corpo.GetProperty("tipo").GetString());
        Assert.Equal("Verificar o sistema de freios", corpo.GetProperty("descricao").GetString());
        Assert.Equal("mec-1", corpo.GetProperty("responsavel").GetString());
        Assert.Equal(primeiro, corpo.GetProperty("registradaEm").GetDateTimeOffset());

        ambiente.Relogio.Agora = segundo;
        using var administrador = ambiente.Fabrica.Cliente("Administrador", "adm-9");
        Assert.Equal(HttpStatusCode.Created, (await administrador.PostAsJsonAsync(Rota(ordem.Id), new { tipo = "reparo", descricao = "Trocar pastilhas" })).StatusCode);

        var salva = await ambiente.Fabrica.NoBancoAsync(db => db.Ordens.Include(o => o.Etapas).SingleAsync(o => o.Id == ordem.Id));
        Assert.Equal(EstadoExecucao.EmExecucao, salva.Estado);
        Assert.Equal(segundo, salva.AtualizadaEm);
        var etapas = salva.Etapas.OrderBy(e => e.Sequencia).ToList();
        Assert.Equal([1, 2], etapas.Select(e => e.Sequencia).ToArray());
        Assert.Equal([TipoEtapa.Diagnostico, TipoEtapa.Reparo], etapas.Select(e => e.Tipo).ToArray());
        Assert.Equal(["Verificar o sistema de freios", "Trocar pastilhas"], etapas.Select(e => e.Descricao).ToArray());
        Assert.Equal(["mec-1", "adm-9"], etapas.Select(e => e.Responsavel).ToArray());
        Assert.Equal([primeiro, segundo], etapas.Select(e => e.RegistradaEm).ToArray());

        // O histórico aparece na fila na ordem em que foi registrado.
        var fila = await (await mecanico.GetAsync("/api/v1/fila-execucao")).Content.ReadFromJsonAsync<JsonElement>();
        var historico = fila.GetProperty("itens")[0].GetProperty("etapas").EnumerateArray().ToList();
        Assert.Equal(["Verificar o sistema de freios", "Trocar pastilhas"], historico.Select(e => e.GetProperty("descricao").GetString()!).ToArray());
        Assert.Equal(["mec-1", "adm-9"], historico.Select(e => e.GetProperty("responsavel").GetString()!).ToArray());
    }

    [Fact]
    public async Task ExecutionStageHistory_DescriptionAtLimitIsAccepted()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var ordem = await ambiente.Fabrica.SemearAsync();
        using var http = ambiente.Fabrica.Cliente();
        var descricao = new string('á', 2000);
        Assert.Equal(HttpStatusCode.Created, (await http.PostAsJsonAsync(Rota(ordem.Id), new { tipo = "Reparo", descricao })).StatusCode);
        Assert.Equal(descricao, (await ambiente.Fabrica.NoBancoAsync(db => db.Etapas.SingleAsync())).Descricao);
    }

    [Fact]
    public async Task ExecutionStageHistory_RequiresAuthenticationAndMechanicOrAdministrator()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var ordem = await ambiente.Fabrica.SemearAsync();
        var corpo = new { tipo = "Diagnostico", descricao = "Checar" };
        using var anonimo = ambiente.Fabrica.Cliente(null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.PostAsJsonAsync(Rota(ordem.Id), corpo)).StatusCode);
        foreach (var papel in new[] { "Atendente", "Cliente", "ServicoOS" })
        {
            using var http = ambiente.Fabrica.Cliente(papel);
            Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync(Rota(ordem.Id), corpo)).StatusCode);
        }
        // Mecânico sem identidade (claim sub) não pode ser registrado como responsável.
        using var semIdentidade = ambiente.Fabrica.Cliente("Mecanico", semSub: true);
        Assert.Equal(HttpStatusCode.Forbidden, (await semIdentidade.PostAsJsonAsync(Rota(ordem.Id), corpo)).StatusCode);
        Assert.Equal(0, await ambiente.Fabrica.NoBancoAsync(db => db.Etapas.CountAsync()));
    }

    [Fact]
    public async Task ExecutionStageHistory_UnknownExecutionReturns404()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        using var http = ambiente.Fabrica.Cliente();
        var resposta = await http.PostAsJsonAsync(Rota(Guid.NewGuid()), new { tipo = "Diagnostico", descricao = "Checar" });
        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
        Assert.Equal("execucao_nao_encontrada", (await resposta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codigo").GetString());
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("\"texto\"")]
    [InlineData("{}")]
    [InlineData("{\"tipo\":1,\"descricao\":\"Checar\"}")]
    [InlineData("{\"tipo\":\"Outro\",\"descricao\":\"Checar\"}")]
    [InlineData("{\"descricao\":\"Checar\"}")]
    [InlineData("{\"tipo\":\"Reparo\"}")]
    [InlineData("{\"tipo\":\"Reparo\",\"descricao\":null}")]
    [InlineData("{\"tipo\":\"Reparo\",\"descricao\":42}")]
    [InlineData("{\"tipo\":\"Reparo\",\"descricao\":\"   \"}")]
    [InlineData("{\"tipo\":\"Reparo\",\"descricao\":\"a\\u0000b\"}")]
    [InlineData("{\"tipo\":\"Reparo\",\"descricao\":\"\\ud800\"}")]
    public async Task ExecutionStageHistory_InvalidBodyReturns422WithoutPersisting(string corpo)
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var ordem = await ambiente.Fabrica.SemearAsync();
        using var http = ambiente.Fabrica.Cliente();

        var resposta = await http.PostAsync(Rota(ordem.Id), Mensagens.Json(corpo));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resposta.StatusCode);
        Assert.Equal("etapa_invalida", (await resposta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codigo").GetString());
        Assert.Equal(0, await ambiente.Fabrica.NoBancoAsync(db => db.Etapas.CountAsync()));
        Assert.Equal(EstadoExecucao.Fila, (await ambiente.Fabrica.NoBancoAsync(db => db.Ordens.SingleAsync())).Estado);
    }

    [Fact]
    public async Task ExecutionStageHistory_DescriptionAboveLimitAndNonJsonContentReturn422()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var ordem = await ambiente.Fabrica.SemearAsync();
        using var http = ambiente.Fabrica.Cliente();
        var longa = await http.PostAsJsonAsync(Rota(ordem.Id), new { tipo = "Reparo", descricao = new string('x', 2001) });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, longa.StatusCode);
        var texto = await http.PostAsync(Rota(ordem.Id), new StringContent("{\"tipo\":\"Reparo\",\"descricao\":\"ok\"}", System.Text.Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, texto.StatusCode);
        Assert.Equal(0, await ambiente.Fabrica.NoBancoAsync(db => db.Etapas.CountAsync()));
    }

    [Theory]
    [InlineData(EstadoExecucao.Concluida)]
    [InlineData(EstadoExecucao.Falhou)]
    public async Task ExecutionStageHistory_StageOnClosedExecutionReturns409(EstadoExecucao estado)
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var ordem = await ambiente.Fabrica.SemearAsync(o =>
        {
            o.RegistrarEtapa(TipoEtapa.Diagnostico, "Primeira", "mec-1", DateTimeOffset.UtcNow);
            if (estado == EstadoExecucao.Concluida) o.Concluir(DateTimeOffset.UtcNow); else o.Falhar("Peça indisponível", DateTimeOffset.UtcNow);
        });
        using var http = ambiente.Fabrica.Cliente();

        var resposta = await http.PostAsJsonAsync(Rota(ordem.Id), new { tipo = "Reparo", descricao = "Tentativa tardia" });

        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        Assert.Equal("execucao_estado_invalido", (await resposta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codigo").GetString());
        Assert.Equal(1, await ambiente.Fabrica.NoBancoAsync(db => db.Etapas.CountAsync()));
    }

    [Fact]
    public async Task ExecutionStageHistory_ConcurrentStagesPersistOneAndReturn409ForTheOther()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var ordem = await ambiente.Fabrica.SemearAsync();
        using var a = ambiente.Fabrica.Cliente("Mecanico", "mec-a");
        using var b = ambiente.Fabrica.Cliente("Mecanico", "mec-b");
        ambiente.Gate.Armar(2);

        var respostas = await Task.WhenAll(
            a.PostAsJsonAsync(Rota(ordem.Id), new { tipo = "Diagnostico", descricao = "Etapa de A" }),
            b.PostAsJsonAsync(Rota(ordem.Id), new { tipo = "Diagnostico", descricao = "Etapa de B" }));
        ambiente.Gate.Armar(0);

        Assert.Equal([201, 409], respostas.Select(r => (int)r.StatusCode).Order().ToArray());
        var perdedora = respostas.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal("conflito_concorrencia", (await perdedora.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codigo").GetString());
        var etapas = await ambiente.Fabrica.NoBancoAsync(db => db.Etapas.ToListAsync());
        var unica = Assert.Single(etapas);
        Assert.Equal(1, unica.Sequencia);
        // Repetir o pedido depois do conflito registra a segunda etapa com a sequência seguinte.
        var vencedora = respostas.Single(r => r.StatusCode == HttpStatusCode.Created);
        Assert.Equal(unica.Id, (await vencedora.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.Created, (await a.PostAsJsonAsync(Rota(ordem.Id), new { tipo = "Reparo", descricao = "Segunda tentativa" })).StatusCode);
        var sequencias = (await ambiente.Fabrica.NoBancoAsync(db => db.Etapas.Select(e => e.Sequencia).ToListAsync())).Order().ToArray();
        Assert.Equal(new[] { 1, 2 }, sequencias);
    }
}
