using AutoReparos.Execucao.Application;
using AutoReparos.Execucao.Domain;
using AutoReparos.Execucao.Infrastructure;

namespace AutoReparos.Execucao.Tests.Suporte;

// Delega todo o IExecucaoStore ao banco real; os testes de falha sobrescrevem só o que querem quebrar.
public abstract class ArmazenamentoDelegante(ExecucaoDbContext real) : IExecucaoStore
{
    public virtual Task<bool> EventoProcessadoAsync(Guid eventId, CancellationToken ct) => real.EventoProcessadoAsync(eventId, ct);
    public Task<OrdemExecucao?> ObterAsync(Guid id, CancellationToken ct) => real.ObterAsync(id, ct);
    public Task<IReadOnlyList<OrdemExecucao>> ListarFilaAsync(IReadOnlyCollection<EstadoExecucao> estados, string? mecanico, CancellationToken ct) => real.ListarFilaAsync(estados, mecanico, ct);
    public Task<OrdemExecucao?> ObterPorOrdemServicoAsync(Guid ordemServicoId, CancellationToken ct) => real.ObterPorOrdemServicoAsync(ordemServicoId, ct);
    public void Adicionar(OrdemExecucao ordem) => real.Adicionar(ordem);
    public void Adicionar(EventoProcessado evento) => real.Adicionar(evento);
    public void Adicionar(MensagemSaida mensagem) => real.Adicionar(mensagem);
    public Task BloquearOrdemAsync(Guid ordemServicoId, CancellationToken ct) => real.BloquearOrdemAsync(ordemServicoId, ct);
    public Task<IReadOnlyList<ReservaInsumo>> ListarReservasAsync(Guid ordemServicoId, CancellationToken ct) => real.ListarReservasAsync(ordemServicoId, ct);
    public Task<IReadOnlyList<ItemEmFalta>> ReservarSaldoAsync(IReadOnlyList<ItemReserva> itens, CancellationToken ct) => real.ReservarSaldoAsync(itens, ct);
    public Task DevolverSaldoAsync(IReadOnlyCollection<ReservaInsumo> reservas, CancellationToken ct) => real.DevolverSaldoAsync(reservas, ct);
    public Task BaixarReservadoAsync(IReadOnlyCollection<ReservaInsumo> reservas, CancellationToken ct) => real.BaixarReservadoAsync(reservas, ct);
    public Task<bool> AdicionarSaldoAsync(Guid insumoId, int quantidade, CancellationToken ct) => real.AdicionarSaldoAsync(insumoId, quantidade, ct);
    public Task<Insumo?> ObterInsumoAsync(Guid id, CancellationToken ct) => real.ObterInsumoAsync(id, ct);
    public Task<IReadOnlyList<Insumo>> ListarInsumosAsync(string? nome, CancellationToken ct) => real.ListarInsumosAsync(nome, ct);
    public Task<IReadOnlyList<ServicoCatalogo>> ListarServicosAsync(string? nome, CancellationToken ct) => real.ListarServicosAsync(nome, ct);
    public void Adicionar(ReservaInsumo reserva) => real.Adicionar(reserva);
    public void Adicionar(MovimentacaoInsumo movimentacao) => real.Adicionar(movimentacao);
    public void Adicionar(Insumo insumo) => real.Adicionar(insumo);
    public void Adicionar(ServicoCatalogo servico) => real.Adicionar(servico);
    public void DescartarAlteracoes() => real.DescartarAlteracoes();
    public Task SalvarAsync(CancellationToken ct) => real.SalvarAsync(ct);
}
