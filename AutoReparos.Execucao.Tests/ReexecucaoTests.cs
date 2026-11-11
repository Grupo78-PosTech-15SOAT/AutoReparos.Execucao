using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AutoReparos.Execucao.Domain;
using AutoReparos.Execucao.Tests.Suporte;
using Microsoft.EntityFrameworkCore;

namespace AutoReparos.Execucao.Tests;

[Collection("postgres")]
public sealed class ReexecucaoTests(PostgresFixture postgres)
{
    private static string Falha(Guid id) => $"/api/v1/execucoes/{id}/falha";
    private static string Reexecucao(Guid id) => $"/api/v1/execucoes/{id}/reexecucao";
    private static readonly DateTimeOffset T0 = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    private static Task<OrdemExecucao> ComDuasEtapasAsync(Ambiente a, bool falhar = false) => a.Fabrica.SemearAsync(o =>
    {
        o.RegistrarEtapa(TipoEtapa.Diagnostico, "Diagnóstico do motor", "mec-1", T0);
        o.RegistrarEtapa(TipoEtapa.Reparo, "Troca da correia", "mec-2", T0.AddMinutes(30));
        if (falhar) o.Falhar("Correia errada", T0.AddHours(1));
    });

    [Fact]
    public async Task ManualReexecutionPreservesStages_FailureMarksInterventionAndKeepsStages()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var ordem = await ComDuasEtapasAsync(ambiente);
        ambiente.Relogio.Agora = T0.AddHours(1);
        using var mecanico = ambiente.Fabrica.Cliente("Mecanico", "mec-1");

        var resposta = await mecanico.PostAsJsonAsync(Falha(ordem.Id), new { motivo = "Correia errada" });

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var corpo = await resposta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Falhou", corpo.GetProperty("status").GetString());
        Assert.True(corpo.GetProperty("intervencaoRequerida").GetBoolean());
        var salva = await ambiente.Fabrica.NoBancoAsync(db => db.Ordens.Include(o => o.Etapas).SingleAsync());
        Assert.Equal(EstadoExecucao.Falhou, salva.Estado);
        Assert.True(salva.IntervencaoRequerida);
        Assert.Equal("Correia errada", salva.MotivoFalha);
        Assert.Equal(T0.AddHours(1), salva.FalhouEm);
        Assert.Equal(["Diagnóstico do motor", "Troca da correia"], salva.Etapas.OrderBy(e => e.Sequencia).Select(e => e.Descricao).ToArray());
        // A falha é comunicada ao OS Service pelo outbox (e só a ele).
        var saida = Assert.Single(await ambiente.Fabrica.NoBancoAsync(db => db.MensagensSaida.ToListAsync()));
        Assert.Equal(DestinoOs, saida.Destino);
        Assert.Equal("execucao.falhou.v1", saida.Tipo);
        var evento = JsonDocument.Parse(saida.Conteudo).RootElement;
        Assert.Equal(saida.EventId, evento.GetProperty("eventId").GetGuid());
        Assert.Equal(ordem.OrdemServicoId, evento.GetProperty("ordemServicoId").GetGuid());
        Assert.Equal("Correia errada", evento.GetProperty("motivo").GetString());
    }

    private const string DestinoOs = AutoReparos.Execucao.Application.DestinosEvento.OsService;

    [Fact]
    public async Task ManualReexecutionPreservesStages_ReexecutionAfterFailureKeepsStagesAndRecordsSupervisor()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var ordem = await ComDuasEtapasAsync(ambiente, falhar: true);
        var antes = await ambiente.Fabrica.NoBancoAsync(db => db.Etapas.OrderBy(e => e.Sequencia).ToListAsync());
        var momento = T0.AddHours(3);
        ambiente.Relogio.Agora = momento;
        using var supervisor = ambiente.Fabrica.Cliente("Administrador", "sup-7");

        var resposta = await supervisor.PostAsJsonAsync(Reexecucao(ordem.Id), new { motivo = "Correia correta instalada" });

        Assert.Equal(HttpStatusCode.Accepted, resposta.StatusCode);
        var corpo = await resposta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("EmExecucao", corpo.GetProperty("status").GetString());
        Assert.False(corpo.GetProperty("intervencaoRequerida").GetBoolean());
        Assert.Equal(2, corpo.GetProperty("etapasPreservadas").GetInt32());
        Assert.Equal("sup-7", corpo.GetProperty("supervisor").GetString());

        var salva = await ambiente.Fabrica.NoBancoAsync(db => db.Ordens.Include(o => o.Etapas).Include(o => o.Reexecucoes).SingleAsync());
        Assert.Equal(EstadoExecucao.EmExecucao, salva.Estado);
        Assert.False(salva.IntervencaoRequerida);
        var depois = salva.Etapas.OrderBy(e => e.Sequencia).ToList();
        Assert.Equal(antes.Select(e => (e.Id, e.Sequencia, e.Tipo, e.Descricao, e.Responsavel, e.RegistradaEm)).ToArray(),
            depois.Select(e => (e.Id, e.Sequencia, e.Tipo, e.Descricao, e.Responsavel, e.RegistradaEm)).ToArray());
        var registro = Assert.Single(salva.Reexecucoes);
        Assert.Equal(("Correia correta instalada", "sup-7", momento), (registro.Motivo, registro.Supervisor, registro.SolicitadaEm));

        // Depois da correção a execução segue: nova etapa e conclusão.
        using var mecanico = ambiente.Fabrica.Cliente("Mecanico", "mec-1");
        Assert.Equal(HttpStatusCode.Created, (await mecanico.PostAsJsonAsync($"/api/v1/execucoes/{ordem.Id}/etapas", new { tipo = "Reparo", descricao = "Reinstalação" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await mecanico.PostAsync($"/api/v1/execucoes/{ordem.Id}/conclusao", null)).StatusCode);
        Assert.Equal(3, await ambiente.Fabrica.NoBancoAsync(db => db.Etapas.CountAsync()));
    }

    [Fact]
    public async Task ManualReexecutionPreservesStages_FailureWithoutStagesReturnsToQueue()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var ordem = await ambiente.Fabrica.SemearAsync(o => o.Falhar("Veículo não chegou", T0));
        using var supervisor = ambiente.Fabrica.Cliente("Administrador", "sup-7");

        var resposta = await supervisor.PostAsJsonAsync(Reexecucao(ordem.Id), new { motivo = "Veículo chegou" });

        Assert.Equal(HttpStatusCode.Accepted, resposta.StatusCode);
        Assert.Equal("Fila", (await resposta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Equal(EstadoExecucao.Fila, (await ambiente.Fabrica.NoBancoAsync(db => db.Ordens.SingleAsync())).Estado);
    }

    [Fact]
    public async Task ManualReexecutionPreservesStages_RequiresSupervisor()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var ordem = await ComDuasEtapasAsync(ambiente, falhar: true);
        var corpo = new { motivo = "Corrigido" };
        using var anonimo = ambiente.Fabrica.Cliente(null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.PostAsJsonAsync(Reexecucao(ordem.Id), corpo)).StatusCode);
        foreach (var papel in new[] { "Mecanico", "Atendente", "Cliente", "ServicoOS" })
        {
            using var http = ambiente.Fabrica.Cliente(papel);
            Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync(Reexecucao(ordem.Id), corpo)).StatusCode);
        }
        using var semIdentidade = ambiente.Fabrica.Cliente("Administrador", semSub: true);
        Assert.Equal(HttpStatusCode.Forbidden, (await semIdentidade.PostAsJsonAsync(Reexecucao(ordem.Id), corpo)).StatusCode);
        var intacta = await ambiente.Fabrica.NoBancoAsync(db => db.Ordens.Include(o => o.Reexecucoes).SingleAsync());
        Assert.Equal(EstadoExecucao.Falhou, intacta.Estado);
        Assert.Empty(intacta.Reexecucoes);
    }

    [Fact]
    public async Task ManualReexecutionPreservesStages_UnknownExecutionReturns404()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        using var supervisor = ambiente.Fabrica.Cliente("Administrador");
        var resposta = await supervisor.PostAsJsonAsync(Reexecucao(Guid.NewGuid()), new { motivo = "Corrigido" });
        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
        Assert.Equal("execucao_nao_encontrada", (await resposta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codigo").GetString());
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"motivo\":null}")]
    [InlineData("{\"motivo\":\"  \"}")]
    [InlineData("{\"motivo\":7}")]
    [InlineData("{\"motivo\":\"a\\u0000b\"}")]
    [InlineData("{\"motivo\":\"\\udc00\"}")]
    public async Task ManualReexecutionPreservesStages_MissingOrInvalidReasonReturns422(string corpo)
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var ordem = await ComDuasEtapasAsync(ambiente, falhar: true);
        using var supervisor = ambiente.Fabrica.Cliente("Administrador");

        var resposta = await supervisor.PostAsync(Reexecucao(ordem.Id), Mensagens.Json(corpo));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resposta.StatusCode);
        Assert.Equal("reexecucao_invalida", (await resposta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codigo").GetString());
        Assert.Equal(EstadoExecucao.Falhou, (await ambiente.Fabrica.NoBancoAsync(db => db.Ordens.SingleAsync())).Estado);
        Assert.Equal(0, await ambiente.Fabrica.NoBancoAsync(db => db.Reexecucoes.CountAsync()));
    }

    [Theory]
    [InlineData(EstadoExecucao.Fila)]
    [InlineData(EstadoExecucao.EmExecucao)]
    [InlineData(EstadoExecucao.Concluida)]
    public async Task ManualReexecutionPreservesStages_ExecutionNotInFailureReturns409(EstadoExecucao estado)
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var ordem = await ambiente.Fabrica.SemearAsync(o =>
        {
            if (estado == EstadoExecucao.Fila) return;
            o.RegistrarEtapa(TipoEtapa.Diagnostico, "Diagnóstico", "mec-1", T0);
            if (estado == EstadoExecucao.Concluida) o.Concluir(T0.AddMinutes(5));
        });
        using var supervisor = ambiente.Fabrica.Cliente("Administrador");

        var resposta = await supervisor.PostAsJsonAsync(Reexecucao(ordem.Id), new { motivo = "Sem falha" });

        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        Assert.Equal("execucao_estado_invalido", (await resposta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codigo").GetString());
        var intacta = await ambiente.Fabrica.NoBancoAsync(db => db.Ordens.Include(o => o.Reexecucoes).SingleAsync());
        Assert.Equal(estado, intacta.Estado);
        Assert.Empty(intacta.Reexecucoes);
    }

    [Fact]
    public async Task ManualReexecutionPreservesStages_SecondRequestAfterAcceptanceReturns409()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var ordem = await ComDuasEtapasAsync(ambiente, falhar: true);
        using var supervisor = ambiente.Fabrica.Cliente("Administrador");
        Assert.Equal(HttpStatusCode.Accepted, (await supervisor.PostAsJsonAsync(Reexecucao(ordem.Id), new { motivo = "Primeira" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await supervisor.PostAsJsonAsync(Reexecucao(ordem.Id), new { motivo = "Segunda" })).StatusCode);
        Assert.Equal(1, await ambiente.Fabrica.NoBancoAsync(db => db.Reexecucoes.CountAsync()));
    }

    [Fact]
    public async Task ManualReexecutionPreservesStages_ConcurrentRequestsAcceptOneAndConflictTheOther()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var ordem = await ComDuasEtapasAsync(ambiente, falhar: true);
        using var a = ambiente.Fabrica.Cliente("Administrador", "sup-a");
        using var b = ambiente.Fabrica.Cliente("Administrador", "sup-b");
        ambiente.Gate.Armar(2);

        var respostas = await Task.WhenAll(a.PostAsJsonAsync(Reexecucao(ordem.Id), new { motivo = "Pedido A" }), b.PostAsJsonAsync(Reexecucao(ordem.Id), new { motivo = "Pedido B" }));
        ambiente.Gate.Armar(0);

        Assert.Equal([202, 409], respostas.Select(r => (int)r.StatusCode).Order().ToArray());
        Assert.Equal("conflito_concorrencia", (await respostas.Single(r => r.StatusCode == HttpStatusCode.Conflict).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codigo").GetString());
        Assert.Equal(1, await ambiente.Fabrica.NoBancoAsync(db => db.Reexecucoes.CountAsync()));
        Assert.Equal(2, await ambiente.Fabrica.NoBancoAsync(db => db.Etapas.CountAsync()));
    }

    [Fact]
    public async Task ManualReexecutionPreservesStages_FailureEndpointAuthorizationValidationAndStates()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var ordem = await ComDuasEtapasAsync(ambiente);
        using var anonimo = ambiente.Fabrica.Cliente(null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.PostAsJsonAsync(Falha(ordem.Id), new { motivo = "x" })).StatusCode);
        using var atendente = ambiente.Fabrica.Cliente("Atendente");
        Assert.Equal(HttpStatusCode.Forbidden, (await atendente.PostAsJsonAsync(Falha(ordem.Id), new { motivo = "x" })).StatusCode);
        using var mecanico = ambiente.Fabrica.Cliente("Mecanico");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await mecanico.PostAsync(Falha(ordem.Id), Mensagens.Json("{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await mecanico.PostAsJsonAsync(Falha(Guid.NewGuid()), new { motivo = "x" })).StatusCode);
        Assert.Equal(EstadoExecucao.EmExecucao, (await ambiente.Fabrica.NoBancoAsync(db => db.Ordens.SingleAsync())).Estado);
        Assert.Equal(HttpStatusCode.OK, (await mecanico.PostAsJsonAsync(Falha(ordem.Id), new { motivo = "Quebrou" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await mecanico.PostAsJsonAsync(Falha(ordem.Id), new { motivo = "Quebrou de novo" })).StatusCode);
        Assert.Equal(1, await ambiente.Fabrica.NoBancoAsync(db => db.MensagensSaida.CountAsync()));
    }
}
