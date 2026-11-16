using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AutoReparos.Execucao.Application;
using AutoReparos.Execucao.Domain;
using AutoReparos.Execucao.Tests.Suporte;
using Microsoft.EntityFrameworkCore;

namespace AutoReparos.Execucao.IntegrationTests;

// Reserva de insumos em duas fases: PostgreSQL real via Testcontainers e comandos injetados na fila de entrada (sem AWS).
[Collection("postgres")]
public sealed class ReservaInsumosTests(PostgresFixture postgres)
{
    private static async Task<List<MensagemSaida>> SaidaAsync(Ambiente ambiente) =>
        (await ambiente.Fabrica.NoBancoAsync(db => db.MensagensSaida.AsNoTracking().ToListAsync())).OrderBy(m => m.CriadoEm).ThenBy(m => m.Tipo).ToList();

    private static Task<List<ReservaInsumo>> ReservasAsync(Ambiente ambiente, Guid os) =>
        ambiente.Fabrica.NoBancoAsync(db => db.Reservas.AsNoTracking().Where(r => r.OrdemServicoId == os).OrderBy(r => r.InsumoId).ToListAsync());

    private static (int Disponivel, int Reservada) Saldo(Insumo i) => (i.QuantidadeDisponivel, i.QuantidadeReservada);

    private static Task Processar(Ambiente ambiente, Amazon.SQS.Model.Message mensagem) => ambiente.Consumidor.ProcessarAsync(mensagem, CancellationToken.None);

    [Fact]
    public async Task ReservaTodosItensComOutbox()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var oleo = await ambiente.Fabrica.SemearInsumoAsync("Óleo", 10);
        var filtro = await ambiente.Fabrica.SemearInsumoAsync("Filtro", 5);
        Guid eventId = Guid.NewGuid(), os = Guid.NewGuid(), correlacao = Guid.NewGuid();

        await Processar(ambiente, Mensagens.ReservarInsumos(eventId, os, [(oleo.Id, 3), (filtro.Id, 2)], correlacao));

        Assert.Equal((7, 3), Saldo(await ambiente.Fabrica.InsumoAsync(oleo.Id)));
        Assert.Equal((3, 2), Saldo(await ambiente.Fabrica.InsumoAsync(filtro.Id)));
        var reservas = await ReservasAsync(ambiente, os);
        Assert.Equal(2, reservas.Count);
        Assert.All(reservas, r => Assert.Equal(StatusReserva.Reservada, r.Status));
        Assert.Equal(1, await ambiente.Fabrica.NoBancoAsync(db => db.EventosProcessados.CountAsync(e => e.EventId == eventId)));
        var saida = Assert.Single(await SaidaAsync(ambiente));
        Assert.Equal((DestinosEvento.OsService, "insumos.reservados.v1", (DateTimeOffset?)null), (saida.Destino, saida.Tipo, saida.EnviadoEm));
        var corpo = JsonDocument.Parse(saida.Conteudo).RootElement;
        Assert.Equal(os, corpo.GetProperty("ordemServicoId").GetGuid());
        Assert.Equal(correlacao, corpo.GetProperty("correlacaoId").GetGuid());
        Assert.Equal(saida.EventId, corpo.GetProperty("eventId").GetGuid());
        Assert.Equal(new[] { (oleo.Id, 3), (filtro.Id, 2) }.OrderBy(i => i.Item1), corpo.GetProperty("itens").EnumerateArray().Select(i => (i.GetProperty("insumoId").GetGuid(), i.GetProperty("quantidade").GetInt32())).OrderBy(i => i.Item1));
        Assert.Empty(ambiente.Alertas.Itens);
    }

    [Fact]
    public async Task FaltaDeSaldoNaoReservaNada()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var oleo = await ambiente.Fabrica.SemearInsumoAsync("Óleo", 10);
        var filtro = await ambiente.Fabrica.SemearInsumoAsync("Filtro", 1);
        var inexistente = Guid.NewGuid();
        Guid eventId = Guid.NewGuid(), os = Guid.NewGuid();

        await Processar(ambiente, Mensagens.ReservarInsumos(eventId, os, [(oleo.Id, 3), (filtro.Id, 2), (inexistente, 1)]));

        Assert.Equal((10, 0), Saldo(await ambiente.Fabrica.InsumoAsync(oleo.Id)));
        Assert.Equal((1, 0), Saldo(await ambiente.Fabrica.InsumoAsync(filtro.Id)));
        Assert.Empty(await ReservasAsync(ambiente, os));
        // O evento é registrado na Inbox (segunda transação), então a repetição não reprocessa.
        Assert.Equal(1, await ambiente.Fabrica.NoBancoAsync(db => db.EventosProcessados.CountAsync(e => e.EventId == eventId)));
        var saida = Assert.Single(await SaidaAsync(ambiente));
        Assert.Equal("insumos.reserva-falhou.v1", saida.Tipo);
        var faltas = JsonDocument.Parse(saida.Conteudo).RootElement.GetProperty("itensEmFalta").EnumerateArray()
            .Select(i => (i.GetProperty("insumoId").GetGuid(), i.GetProperty("quantidadeSolicitada").GetInt32(), i.GetProperty("quantidadeDisponivel").GetInt32())).OrderBy(i => i.Item1).ToList();
        Assert.Equal(new[] { (filtro.Id, 2, 1), (inexistente, 1, 0) }.OrderBy(i => i.Item1), faltas);
        Assert.Empty(ambiente.Alertas.Itens);
    }

    [Fact]
    public async Task ReservaNaoDuplica()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var oleo = await ambiente.Fabrica.SemearInsumoAsync("Óleo", 10);
        Guid eventId = Guid.NewGuid(), os = Guid.NewGuid();
        var comando = Mensagens.ReservarInsumos(eventId, os, [(oleo.Id, 4)]);

        await Processar(ambiente, comando);
        await Processar(ambiente, comando);
        // Outro comando da mesma OS (eventId novo, quantidade diferente): não reserva de novo e a Saga recebe o mesmo desfecho.
        await Processar(ambiente, Mensagens.ReservarInsumos(Guid.NewGuid(), os, [(oleo.Id, 9)]));

        Assert.Equal((6, 4), Saldo(await ambiente.Fabrica.InsumoAsync(oleo.Id)));
        var reserva = Assert.Single(await ReservasAsync(ambiente, os));
        Assert.Equal((4, StatusReserva.Reservada), (reserva.Quantidade, reserva.Status));
        var saida = await SaidaAsync(ambiente);
        Assert.Equal(["insumos.reservados.v1", "insumos.reservados.v1"], saida.Select(m => m.Tipo));
        Assert.Equal(4, JsonDocument.Parse(saida[1].Conteudo).RootElement.GetProperty("itens")[0].GetProperty("quantidade").GetInt32());
    }

    [Fact]
    public async Task ReservasConcorrentesRespeitamSaldo()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var oleo = await ambiente.Fabrica.SemearInsumoAsync("Óleo", 10);
        var ordens = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToList();

        await Task.WhenAll(ordens.Select(os => Task.Run(() => Processar(ambiente, Mensagens.ReservarInsumos(Guid.NewGuid(), os, [(oleo.Id, 2)])))));

        Assert.Equal((0, 10), Saldo(await ambiente.Fabrica.InsumoAsync(oleo.Id)));
        var saida = await SaidaAsync(ambiente);
        Assert.Equal((5, 3), (saida.Count(m => m.Tipo == "insumos.reservados.v1"), saida.Count(m => m.Tipo == "insumos.reserva-falhou.v1")));
        Assert.Equal(5, await ambiente.Fabrica.NoBancoAsync(db => db.Reservas.CountAsync()));
        Assert.Empty(ambiente.Alertas.Itens);
    }

    [Fact]
    public async Task ReservasConcorrentesDaMesmaOsTemUmSoDesfecho()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        // Saldo para um só dos dois comandos (eventIds diferentes): o segundo deve enxergar a reserva do primeiro e repetir o desfecho, nunca "falhou".
        for (var rodada = 0; rodada < 6; rodada++)
        {
            var oleo = await ambiente.Fabrica.SemearInsumoAsync("Óleo " + rodada, 3);
            var os = Guid.NewGuid();

            await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => Processar(ambiente, Mensagens.ReservarInsumos(Guid.NewGuid(), os, [(oleo.Id, 2)])))));

            Assert.Equal((1, 2), Saldo(await ambiente.Fabrica.InsumoAsync(oleo.Id)));
            Assert.Single(await ReservasAsync(ambiente, os));
        }
        var saida = await SaidaAsync(ambiente);
        Assert.Equal(12, saida.Count);
        Assert.All(saida, m => Assert.Equal("insumos.reservados.v1", m.Tipo));
    }

    [Fact]
    public async Task ConclusaoConfirmaReservas()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var oleo = await ambiente.Fabrica.SemearInsumoAsync("Óleo", 10);
        var ordem = await ambiente.Fabrica.SemearAsync(o => o.RegistrarEtapa(TipoEtapa.Reparo, "Troca de óleo", "mec-1", DateTimeOffset.UtcNow));
        await Processar(ambiente, Mensagens.ReservarInsumos(Guid.NewGuid(), ordem.OrdemServicoId, [(oleo.Id, 4)]));
        using var mecanico = ambiente.Fabrica.Cliente("Mecanico", "mec-1");

        Assert.Equal(HttpStatusCode.OK, (await mecanico.PostAsync($"/api/v1/execucoes/{ordem.Id}/conclusao", null)).StatusCode);

        // Baixa definitiva: sai do reservado, o disponível não volta.
        Assert.Equal((6, 0), Saldo(await ambiente.Fabrica.InsumoAsync(oleo.Id)));
        Assert.Equal(StatusReserva.Confirmada, Assert.Single(await ReservasAsync(ambiente, ordem.OrdemServicoId)).Status);
        var saida = await SaidaAsync(ambiente);
        Assert.Equal(2, saida.Count(m => m.Tipo == "execucao.concluida.v1"));

        // Concluir uma OS sem reserva continua válido e não altera saldo.
        var semReserva = await ambiente.Fabrica.SemearAsync(o => o.RegistrarEtapa(TipoEtapa.Reparo, "Revisão", "mec-1", DateTimeOffset.UtcNow));
        Assert.Equal(HttpStatusCode.OK, (await mecanico.PostAsync($"/api/v1/execucoes/{semReserva.Id}/conclusao", null)).StatusCode);
        Assert.Equal((6, 0), Saldo(await ambiente.Fabrica.InsumoAsync(oleo.Id)));
    }

    [Fact]
    public async Task CancelarReservaDevolveSaldo()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var oleo = await ambiente.Fabrica.SemearInsumoAsync("Óleo", 10);
        var filtro = await ambiente.Fabrica.SemearInsumoAsync("Filtro", 5);
        var os = Guid.NewGuid(); var correlacao = Guid.NewGuid();
        await Processar(ambiente, Mensagens.ReservarInsumos(Guid.NewGuid(), os, [(oleo.Id, 3), (filtro.Id, 2)]));
        // A reserva do filtro já foi confirmada (baixa feita): a compensação não pode devolvê-la.
        await ambiente.Fabrica.NoBancoAsync(async db =>
        {
            var reserva = await db.Reservas.SingleAsync(r => r.OrdemServicoId == os && r.InsumoId == filtro.Id);
            reserva.Confirmar(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
            return 0;
        });
        var cancelar = Mensagens.CancelarReserva(Guid.NewGuid(), os, correlacao);

        await Processar(ambiente, cancelar);

        Assert.Equal((10, 0), Saldo(await ambiente.Fabrica.InsumoAsync(oleo.Id)));
        Assert.Equal((3, 2), Saldo(await ambiente.Fabrica.InsumoAsync(filtro.Id)));
        var reservas = (await ReservasAsync(ambiente, os)).ToDictionary(r => r.InsumoId, r => r.Status);
        Assert.Equal((StatusReserva.Cancelada, StatusReserva.Confirmada), (reservas[oleo.Id], reservas[filtro.Id]));
        var cancelada = (await SaidaAsync(ambiente)).Single(m => m.Tipo == "insumos.reserva-cancelada.v1");
        var corpo = JsonDocument.Parse(cancelada.Conteudo).RootElement;
        Assert.Equal((os, correlacao), (corpo.GetProperty("ordemServicoId").GetGuid(), corpo.GetProperty("correlacaoId").GetGuid()));
        var item = Assert.Single(corpo.GetProperty("itens").EnumerateArray());
        Assert.Equal((oleo.Id, 3), (item.GetProperty("insumoId").GetGuid(), item.GetProperty("quantidade").GetInt32()));

        // Entrega repetida (mesmo eventId) e novo comando da mesma OS: saldo não é devolvido de novo.
        await Processar(ambiente, cancelar);
        await Processar(ambiente, Mensagens.CancelarReserva(Guid.NewGuid(), os, correlacao));
        Assert.Equal((10, 0), Saldo(await ambiente.Fabrica.InsumoAsync(oleo.Id)));
        Assert.Equal(2, (await SaidaAsync(ambiente)).Count(m => m.Tipo == "insumos.reserva-cancelada.v1"));
    }

    [Fact]
    public async Task ComandoDeReservaInvalidoVaiParaDlqDeEntrada()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var semItens = Mensagens.ReservarInsumos(Guid.NewGuid(), Guid.NewGuid(), []);
        var quantidadeZero = Mensagens.ReservarInsumos(Guid.NewGuid(), Guid.NewGuid(), [(Guid.NewGuid(), 0)]);
        var insumoRepetido = Guid.NewGuid();
        var somaAcimaDoLimite = Mensagens.ReservarInsumos(Guid.NewGuid(), Guid.NewGuid(), [(insumoRepetido, 600_000), (insumoRepetido, 600_000)]);

        await Processar(ambiente, semItens);
        await Processar(ambiente, quantidadeZero);
        await Processar(ambiente, somaAcimaDoLimite);

        Assert.Equal([(FabricaApi.DlqLiberadas, semItens.Body), (FabricaApi.DlqLiberadas, quantidadeZero.Body), (FabricaApi.DlqLiberadas, somaAcimaDoLimite.Body)], ambiente.Sqs.Enviadas);
        Assert.Empty(await SaidaAsync(ambiente));
        Assert.Equal(0, await ambiente.Fabrica.NoBancoAsync(db => db.Reservas.CountAsync()));
    }
}
