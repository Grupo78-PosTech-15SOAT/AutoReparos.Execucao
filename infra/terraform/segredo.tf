# Senha do usuário da database do serviço. O provisionador da plataforma cria a database e o usuário com ela;
# o job deploy lê só connectionString para o Secret execucao-database do namespace.
locals {
  database = {
    username = "${local.servico}_app"
    dbname   = "${local.servico}_db"
    host     = data.aws_ssm_parameter.database_address.insecure_value
    port     = 5432
  }
}

resource "random_password" "database" {
  length  = 32
  special = false
}

resource "aws_secretsmanager_secret" "database" {
  name                    = "autoreparos/${var.ambiente}/${local.servico}/database"
  description             = "Credencial do usuário ${local.database.username} na database ${local.database.dbname}"
  recovery_window_in_days = 0
}

resource "aws_secretsmanager_secret_version" "database" {
  secret_id = aws_secretsmanager_secret.database.id
  secret_string = jsonencode(merge(local.database, {
    password         = random_password.database.result
    connectionString = "Host=${local.database.host};Port=${local.database.port};Database=${local.database.dbname};Username=${local.database.username};Password=${random_password.database.result};SSL Mode=Prefer;"
  }))
}
