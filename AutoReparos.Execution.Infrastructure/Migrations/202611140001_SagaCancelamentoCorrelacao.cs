using AutoReparos.Execution.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AutoReparos.Execution.Infrastructure.Migrations;

// Saga: correlação da OS liberada e cancelamento da execução (execucao.cancelar.v1). Colunas nulas: linhas antigas continuam válidas.
[DbContext(typeof(ExecucaoDbContext))]
[Migration("202611140001_SagaCancelamentoCorrelacao")]
public sealed class SagaCancelamentoCorrelacao : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>("CorrelacaoId", "ordens_execucao", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<string>("MotivoCancelamento", "ordens_execucao", type: "character varying(2000)", maxLength: 2000, nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>("CanceladaEm", "ordens_execucao", type: "timestamp with time zone", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("CanceladaEm", "ordens_execucao");
        migrationBuilder.DropColumn("MotivoCancelamento", "ordens_execucao");
        migrationBuilder.DropColumn("CorrelacaoId", "ordens_execucao");
    }
}
