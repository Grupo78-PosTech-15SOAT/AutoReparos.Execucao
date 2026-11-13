using AutoReparos.Execucao.Application;
using AutoReparos.Execucao.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AutoReparos.Execucao.Infrastructure;

public sealed class ExecucaoDbContext(DbContextOptions<ExecucaoDbContext> options) : DbContext(options), IExecucaoStore
{
    public DbSet<OrdemExecucao> Ordens => Set<OrdemExecucao>();
    public DbSet<EtapaExecucao> Etapas => Set<EtapaExecucao>();
    public DbSet<ReexecucaoExecucao> Reexecucoes => Set<ReexecucaoExecucao>();
    public DbSet<EventoProcessado> EventosProcessados => Set<EventoProcessado>();
    public DbSet<MensagemSaida> MensagensSaida => Set<MensagemSaida>();
    public DbSet<ServicoCatalogo> Servicos => Set<ServicoCatalogo>();
    public DbSet<Insumo> Insumos => Set<Insumo>();
    public DbSet<MovimentacaoInsumo> MovimentacoesInsumo => Set<MovimentacaoInsumo>();
    public DbSet<ReservaInsumo> Reservas => Set<ReservaInsumo>();

    // Transação aberta pelos métodos de saldo; confirmada em SalvarAsync, desfeita em DescartarAlteracoes ou quando a reserva falta saldo.
    private IDbContextTransaction? _transacao;

    protected override void OnModelCreating(ModelBuilder model)
    {
        var ordem = model.Entity<OrdemExecucao>();
        ordem.ToTable("ordens_execucao").HasKey(x => x.Id);
        ordem.Property(x => x.Id).ValueGeneratedNever();
        ordem.Property<uint>("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        ordem.HasIndex(x => x.OrdemServicoId).IsUnique();
        ordem.HasIndex(x => new { x.Estado, x.CriadaEm });
        ordem.Property(x => x.Estado).HasConversion<int>();
        ordem.Property(x => x.MotivoFalha).HasMaxLength(LimitesExecucao.TextoMaximo);
        ordem.Property(x => x.MotivoCancelamento).HasMaxLength(LimitesExecucao.TextoMaximo);
        ordem.HasMany(x => x.Etapas).WithOne().HasForeignKey(x => x.OrdemExecucaoId);
        ordem.Navigation(x => x.Etapas).UsePropertyAccessMode(PropertyAccessMode.Field);
        ordem.HasMany(x => x.Reexecucoes).WithOne().HasForeignKey(x => x.OrdemExecucaoId);
        ordem.Navigation(x => x.Reexecucoes).UsePropertyAccessMode(PropertyAccessMode.Field);

        var etapa = model.Entity<EtapaExecucao>();
        etapa.ToTable("etapas_execucao").HasKey(x => x.Id);
        etapa.Property(x => x.Id).ValueGeneratedNever();
        etapa.Property(x => x.Tipo).HasConversion<int>();
        etapa.Property(x => x.Descricao).HasMaxLength(LimitesExecucao.TextoMaximo).IsRequired();
        etapa.Property(x => x.Responsavel).HasMaxLength(LimitesExecucao.IdentidadeMaxima).IsRequired();
        etapa.HasIndex(x => new { x.OrdemExecucaoId, x.Sequencia }).IsUnique();
        etapa.HasIndex(x => x.Responsavel);

        var reexecucao = model.Entity<ReexecucaoExecucao>();
        reexecucao.ToTable("reexecucoes_execucao").HasKey(x => x.Id);
        reexecucao.Property(x => x.Id).ValueGeneratedNever();
        reexecucao.Property(x => x.Motivo).HasMaxLength(LimitesExecucao.TextoMaximo).IsRequired();
        reexecucao.Property(x => x.Supervisor).HasMaxLength(LimitesExecucao.IdentidadeMaxima).IsRequired();

        var evento = model.Entity<EventoProcessado>();
        evento.ToTable("eventos_processados").HasKey(x => x.EventId);
        evento.Property(x => x.EventId).ValueGeneratedNever();

        var saida = model.Entity<MensagemSaida>();
        saida.ToTable("mensagens_saida").HasKey(x => x.Id);
        saida.Property(x => x.Id).ValueGeneratedNever();
        saida.Property(x => x.Destino).HasMaxLength(40).IsRequired();
        saida.Property(x => x.Tipo).HasMaxLength(100).IsRequired();
        saida.HasIndex(x => new { x.EventId, x.Destino }).IsUnique();

        var servico = model.Entity<ServicoCatalogo>();
        servico.ToTable("servicos_catalogo").HasKey(x => x.Id);
        servico.Property(x => x.Id).ValueGeneratedNever();
        servico.Property(x => x.Nome).HasMaxLength(LimitesCatalogo.NomeMaximo).IsRequired();
        servico.Property(x => x.Descricao).HasMaxLength(LimitesCatalogo.DescricaoMaxima);
        servico.Property(x => x.PrecoUnitario).HasColumnType("numeric(18,2)");

        var insumo = model.Entity<Insumo>();
        insumo.ToTable("insumos").HasKey(x => x.Id);
        insumo.Property(x => x.Id).ValueGeneratedNever();
        insumo.Property(x => x.Nome).HasMaxLength(LimitesCatalogo.NomeMaximo).IsRequired();
        insumo.Property(x => x.Descricao).HasMaxLength(LimitesCatalogo.DescricaoMaxima);
        insumo.Property(x => x.PrecoUnitario).HasColumnType("numeric(18,2)");

        var movimentacao = model.Entity<MovimentacaoInsumo>();
        movimentacao.ToTable("movimentacoes_insumo").HasKey(x => x.Id);
        movimentacao.Property(x => x.Id).ValueGeneratedNever();
        movimentacao.Property(x => x.Responsavel).HasMaxLength(LimitesExecucao.IdentidadeMaxima).IsRequired();
        movimentacao.HasIndex(x => new { x.InsumoId, x.RegistradaEm });

        var reserva = model.Entity<ReservaInsumo>();
        reserva.ToTable("reservas_insumo").HasKey(x => x.Id);
        reserva.Property(x => x.Id).ValueGeneratedNever();
        reserva.Property<uint>("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        reserva.Property(x => x.Status).HasConversion<int>();
        reserva.HasIndex(x => new { x.OrdemServicoId, x.InsumoId }).IsUnique();
    }

    public override void Dispose() { _transacao?.Dispose(); base.Dispose(); }

    public Task<OrdemExecucao?> ObterAsync(Guid id, CancellationToken ct) =>
        Ordens.Include(x => x.Etapas).Include(x => x.Reexecucoes).SingleOrDefaultAsync(x => x.Id == id, ct);

    public async Task<IReadOnlyList<OrdemExecucao>> ListarFilaAsync(IReadOnlyCollection<EstadoExecucao> estados, string? mecanico, CancellationToken ct)
    {
        var estadosArray = estados.ToArray();
        var consulta = Ordens.AsNoTracking().Include(x => x.Etapas).Where(x => estadosArray.Contains(x.Estado));
        if (mecanico is not null) consulta = consulta.Where(x => x.Etapas.Any(e => e.Responsavel == mecanico));
        return await consulta.OrderBy(x => x.CriadaEm).ThenBy(x => x.Id).ToListAsync(ct);
    }

    public Task<bool> EventoProcessadoAsync(Guid eventId, CancellationToken ct) => EventosProcessados.AnyAsync(x => x.EventId == eventId, ct);
    public Task<OrdemExecucao?> ObterPorOrdemServicoAsync(Guid ordemServicoId, CancellationToken ct) =>
        Ordens.Include(x => x.Etapas).Include(x => x.Reexecucoes).SingleOrDefaultAsync(x => x.OrdemServicoId == ordemServicoId, ct);
    public void Adicionar(OrdemExecucao ordem) => Ordens.Add(ordem);
    public void Adicionar(EventoProcessado evento) => EventosProcessados.Add(evento);
    public void Adicionar(MensagemSaida mensagem) => MensagensSaida.Add(mensagem);
    public void Adicionar(ReservaInsumo reserva) => Reservas.Add(reserva);
    public void Adicionar(MovimentacaoInsumo movimentacao) => MovimentacoesInsumo.Add(movimentacao);
    public void Adicionar(Insumo insumo) => Insumos.Add(insumo);
    public void Adicionar(ServicoCatalogo servico) => Servicos.Add(servico);

    public void DescartarAlteracoes()
    {
        _transacao?.Dispose(); _transacao = null;
        ChangeTracker.Clear();
    }

    public async Task BloquearOrdemAsync(Guid ordemServicoId, CancellationToken ct)
    {
        await IniciarTransacaoAsync(ct);
        // Liberada no commit ou rollback; quem chega depois espera e já enxerga a reserva do vencedor.
        await Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({ordemServicoId.ToString()}, 0))", ct);
    }

    public async Task<IReadOnlyList<ReservaInsumo>> ListarReservasAsync(Guid ordemServicoId, CancellationToken ct) =>
        await Reservas.Where(x => x.OrdemServicoId == ordemServicoId).OrderBy(x => x.InsumoId).ToListAsync(ct);

    public async Task<IReadOnlyList<ItemEmFalta>> ReservarSaldoAsync(IReadOnlyList<ItemReserva> itens, CancellationToken ct)
    {
        await IniciarTransacaoAsync(ct);
        var faltas = new List<ItemReserva>();
        foreach (var item in itens.OrderBy(i => i.InsumoId))
        {
            // Atômico: só desconta se houver saldo; duas reservas concorrentes nunca deixam o disponível negativo.
            var linhas = await Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE insumos SET \"QuantidadeDisponivel\" = \"QuantidadeDisponivel\" - {item.Quantidade}, \"QuantidadeReservada\" = \"QuantidadeReservada\" + {item.Quantidade} WHERE \"Id\" = {item.InsumoId} AND \"QuantidadeDisponivel\" >= {item.Quantidade}", ct);
            if (linhas == 0) faltas.Add(item);
        }
        if (faltas.Count == 0) return [];
        await DescartarTransacaoAsync();
        var ids = faltas.Select(f => f.InsumoId).ToArray();
        var saldos = await Insumos.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.QuantidadeDisponivel, ct);
        return [.. faltas.Select(f => new ItemEmFalta(f.InsumoId, f.Quantidade, saldos.GetValueOrDefault(f.InsumoId)))];
    }

    public async Task DevolverSaldoAsync(IReadOnlyCollection<ReservaInsumo> reservas, CancellationToken ct)
    {
        if (reservas.Count == 0) return;
        await IniciarTransacaoAsync(ct);
        foreach (var r in reservas.OrderBy(r => r.InsumoId))
            await Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE insumos SET \"QuantidadeDisponivel\" = \"QuantidadeDisponivel\" + {r.Quantidade}, \"QuantidadeReservada\" = \"QuantidadeReservada\" - {r.Quantidade} WHERE \"Id\" = {r.InsumoId}", ct);
    }

    public async Task BaixarReservadoAsync(IReadOnlyCollection<ReservaInsumo> reservas, CancellationToken ct)
    {
        if (reservas.Count == 0) return;
        await IniciarTransacaoAsync(ct);
        foreach (var r in reservas.OrderBy(r => r.InsumoId))
            await Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE insumos SET \"QuantidadeReservada\" = \"QuantidadeReservada\" - {r.Quantidade} WHERE \"Id\" = {r.InsumoId}", ct);
    }

    public async Task<bool> AdicionarSaldoAsync(Guid insumoId, int quantidade, CancellationToken ct)
    {
        await IniciarTransacaoAsync(ct);
        var linhas = await Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE insumos SET \"QuantidadeDisponivel\" = \"QuantidadeDisponivel\" + {quantidade} WHERE \"Id\" = {insumoId} AND \"QuantidadeDisponivel\" + {quantidade} <= {LimitesCatalogo.SaldoMaximo}", ct);
        if (linhas == 0) await DescartarTransacaoAsync();
        return linhas > 0;
    }

    public Task<Insumo?> ObterInsumoAsync(Guid id, CancellationToken ct) => Insumos.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);

    public async Task<IReadOnlyList<Insumo>> ListarInsumosAsync(string? nome, CancellationToken ct)
    {
        var consulta = Insumos.AsNoTracking();
        if (nome is not null) consulta = consulta.Where(x => x.Nome.ToLower().Contains(nome.ToLower()));
        return await consulta.OrderBy(x => x.Nome).ThenBy(x => x.Id).Take(LimitesCatalogo.ListagemMaxima).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ServicoCatalogo>> ListarServicosAsync(string? nome, CancellationToken ct)
    {
        var consulta = Servicos.AsNoTracking();
        if (nome is not null) consulta = consulta.Where(x => x.Nome.ToLower().Contains(nome.ToLower()));
        return await consulta.OrderBy(x => x.Nome).ThenBy(x => x.Id).Take(LimitesCatalogo.ListagemMaxima).ToListAsync(ct);
    }

    private async Task IniciarTransacaoAsync(CancellationToken ct) => _transacao ??= await Database.BeginTransactionAsync(ct);

    private async Task DescartarTransacaoAsync()
    {
        if (_transacao is null) return;
        await _transacao.DisposeAsync(); _transacao = null;
    }

    public async Task SalvarAsync(CancellationToken ct)
    {
        try
        {
            await SaveChangesAsync(ct);
            if (_transacao is not null) await _transacao.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex) { throw new ConcorrenciaException("Registro alterado por outra operação.", ex); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        { throw new ConcorrenciaException("Registro duplicado por outra operação.", ex); }
        finally { await DescartarTransacaoAsync(); }
    }
}
