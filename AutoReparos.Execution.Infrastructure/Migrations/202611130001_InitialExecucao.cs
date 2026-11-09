using AutoReparos.Execution.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AutoReparos.Execution.Infrastructure.Migrations;

[DbContext(typeof(ExecucaoDbContext))]
[Migration("202611130001_InitialExecucao")]
public sealed class InitialExecucao : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("ordens_execucao", table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            OrdemServicoId = table.Column<Guid>(type: "uuid", nullable: false),
            ClienteId = table.Column<Guid>(type: "uuid", nullable: false),
            VeiculoId = table.Column<Guid>(type: "uuid", nullable: false),
            Estado = table.Column<int>(type: "integer", nullable: false),
            IntervencaoRequerida = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
            MotivoFalha = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
            CriadaEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            AtualizadaEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            ConcluidaEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
            FalhouEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
            Revisao = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
        }, constraints: table => table.PrimaryKey("PK_ordens_execucao", x => x.Id));
        migrationBuilder.CreateIndex("IX_ordens_execucao_OrdemServicoId", "ordens_execucao", "OrdemServicoId", unique: true);
        migrationBuilder.CreateIndex("IX_ordens_execucao_Estado_CriadaEm", "ordens_execucao", new[] { "Estado", "CriadaEm" });

        migrationBuilder.CreateTable("etapas_execucao", table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            OrdemExecucaoId = table.Column<Guid>(type: "uuid", nullable: false),
            Sequencia = table.Column<int>(type: "integer", nullable: false),
            Tipo = table.Column<int>(type: "integer", nullable: false),
            Descricao = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
            Responsavel = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
            RegistradaEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_etapas_execucao", x => x.Id);
            table.ForeignKey("FK_etapas_execucao_ordens_execucao_OrdemExecucaoId", x => x.OrdemExecucaoId, "ordens_execucao", "Id", onDelete: ReferentialAction.Cascade);
        });
        migrationBuilder.CreateIndex("IX_etapas_execucao_OrdemExecucaoId_Sequencia", "etapas_execucao", new[] { "OrdemExecucaoId", "Sequencia" }, unique: true);
        migrationBuilder.CreateIndex("IX_etapas_execucao_Responsavel", "etapas_execucao", "Responsavel");

        migrationBuilder.CreateTable("reexecucoes_execucao", table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            OrdemExecucaoId = table.Column<Guid>(type: "uuid", nullable: false),
            Motivo = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
            Supervisor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
            SolicitadaEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_reexecucoes_execucao", x => x.Id);
            table.ForeignKey("FK_reexecucoes_execucao_ordens_execucao_OrdemExecucaoId", x => x.OrdemExecucaoId, "ordens_execucao", "Id", onDelete: ReferentialAction.Cascade);
        });
        migrationBuilder.CreateIndex("IX_reexecucoes_execucao_OrdemExecucaoId", "reexecucoes_execucao", "OrdemExecucaoId");

        migrationBuilder.CreateTable("eventos_processados", table => new
        {
            EventId = table.Column<Guid>(type: "uuid", nullable: false),
            ProcessadoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_eventos_processados", x => x.EventId));

        migrationBuilder.CreateTable("mensagens_saida", table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            EventId = table.Column<Guid>(type: "uuid", nullable: false),
            Destino = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
            Tipo = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
            Conteudo = table.Column<string>(type: "text", nullable: false),
            CriadoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            EnviadoEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
            Tentativas = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
            ProximaTentativaEm = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
            EncaminhadoParaDlq = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
        }, constraints: table => table.PrimaryKey("PK_mensagens_saida", x => x.Id));
        migrationBuilder.CreateIndex("IX_mensagens_saida_EventId_Destino", "mensagens_saida", new[] { "EventId", "Destino" }, unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("mensagens_saida"); migrationBuilder.DropTable("eventos_processados");
        migrationBuilder.DropTable("reexecucoes_execucao"); migrationBuilder.DropTable("etapas_execucao"); migrationBuilder.DropTable("ordens_execucao");
    }
}
