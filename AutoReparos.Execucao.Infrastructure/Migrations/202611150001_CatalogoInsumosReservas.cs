using AutoReparos.Execucao.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AutoReparos.Execucao.Infrastructure.Migrations;

// Catálogo de serviços, insumos com saldo disponível e reservado, movimentações de entrada e reservas em duas fases (feature insumos).
// O seed é próprio da Execução: nenhum dado é migrado do App.
[DbContext(typeof(ExecucaoDbContext))]
[Migration("202611150001_CatalogoInsumosReservas")]
public sealed class CatalogoInsumosReservas : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("servicos_catalogo", table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            Nome = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
            Descricao = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
            PrecoUnitario = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
            CriadoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_servicos_catalogo", x => x.Id));

        migrationBuilder.CreateTable("insumos", table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            Nome = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
            Descricao = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
            PrecoUnitario = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
            QuantidadeDisponivel = table.Column<int>(type: "integer", nullable: false),
            QuantidadeReservada = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
            CriadoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_insumos", x => x.Id);
            // Última defesa da reserva atômica: nenhum saldo fica negativo, mesmo sob concorrência.
            table.CheckConstraint("CK_insumos_saldos_nao_negativos", "\"QuantidadeDisponivel\" >= 0 AND \"QuantidadeReservada\" >= 0");
        });

        migrationBuilder.CreateTable("movimentacoes_insumo", table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            InsumoId = table.Column<Guid>(type: "uuid", nullable: false),
            Quantidade = table.Column<int>(type: "integer", nullable: false),
            Responsavel = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
            RegistradaEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_movimentacoes_insumo", x => x.Id);
            table.ForeignKey("FK_movimentacoes_insumo_insumos_InsumoId", x => x.InsumoId, "insumos", "Id");
        });
        migrationBuilder.CreateIndex("IX_movimentacoes_insumo_InsumoId_RegistradaEm", "movimentacoes_insumo", new[] { "InsumoId", "RegistradaEm" });

        migrationBuilder.CreateTable("reservas_insumo", table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            OrdemServicoId = table.Column<Guid>(type: "uuid", nullable: false),
            InsumoId = table.Column<Guid>(type: "uuid", nullable: false),
            Quantidade = table.Column<int>(type: "integer", nullable: false),
            Status = table.Column<int>(type: "integer", nullable: false),
            CriadaEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            AtualizadaEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_reservas_insumo", x => x.Id);
            table.ForeignKey("FK_reservas_insumo_insumos_InsumoId", x => x.InsumoId, "insumos", "Id");
        });
        migrationBuilder.CreateIndex("IX_reservas_insumo_OrdemServicoId_InsumoId", "reservas_insumo", new[] { "OrdemServicoId", "InsumoId" }, unique: true);

        // SQL puro: migrations manuais não têm modelo de destino para InsertData.
        migrationBuilder.Sql("""
            INSERT INTO servicos_catalogo ("Id", "Nome", "Descricao", "PrecoUnitario", "CriadoEm") VALUES
            ('5a2f0001-0000-4000-8000-000000000001', 'Troca de óleo e filtro', 'Troca do óleo do motor e do filtro de óleo', 120.00, '2026-11-15T00:00:00Z'),
            ('5a2f0001-0000-4000-8000-000000000002', 'Alinhamento e balanceamento', 'Alinhamento da direção e balanceamento das quatro rodas', 160.00, '2026-11-15T00:00:00Z'),
            ('5a2f0001-0000-4000-8000-000000000003', 'Revisão de freios', 'Inspeção e ajuste do sistema de freios', 220.00, '2026-11-15T00:00:00Z'),
            ('5a2f0001-0000-4000-8000-000000000004', 'Diagnóstico eletrônico', 'Leitura de falhas com scanner automotivo', 90.00, '2026-11-15T00:00:00Z'),
            ('5a2f0001-0000-4000-8000-000000000005', 'Troca de bateria', 'Substituição e teste da bateria', 60.00, '2026-11-15T00:00:00Z');
            INSERT INTO insumos ("Id", "Nome", "Descricao", "PrecoUnitario", "QuantidadeDisponivel", "QuantidadeReservada", "CriadoEm") VALUES
            ('1a5f0001-0000-4000-8000-000000000001', 'Óleo 5W30 (litro)', 'Óleo sintético para motor', 42.50, 100, 0, '2026-11-15T00:00:00Z'),
            ('1a5f0001-0000-4000-8000-000000000002', 'Filtro de óleo', 'Filtro de óleo universal', 28.90, 40, 0, '2026-11-15T00:00:00Z'),
            ('1a5f0001-0000-4000-8000-000000000003', 'Pastilha de freio (jogo)', 'Jogo de pastilhas dianteiras', 135.00, 20, 0, '2026-11-15T00:00:00Z'),
            ('1a5f0001-0000-4000-8000-000000000004', 'Fluido de freio (frasco)', 'Fluido DOT 4, 500 ml', 24.00, 30, 0, '2026-11-15T00:00:00Z'),
            ('1a5f0001-0000-4000-8000-000000000005', 'Bateria 60Ah', 'Bateria automotiva 60 Ah', 480.00, 8, 0, '2026-11-15T00:00:00Z');
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("reservas_insumo");
        migrationBuilder.DropTable("movimentacoes_insumo");
        migrationBuilder.DropTable("insumos");
        migrationBuilder.DropTable("servicos_catalogo");
    }
}
