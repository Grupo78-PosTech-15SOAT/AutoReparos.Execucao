# A plataforma (repositório pai) publica no SSM o que os serviços precisam; o serviço não lê o state dela (D-53).
locals {
  servico    = "execucao"
  parametros = "/autoreparos/${var.ambiente}"
  conta      = data.aws_caller_identity.atual.account_id
}

data "aws_caller_identity" "atual" {}

data "aws_ssm_parameter" "oidc_provider_arn" {
  name = "${local.parametros}/eks/oidc_provider_arn"
}

data "aws_ssm_parameter" "oidc_provider_url" {
  name = "${local.parametros}/eks/oidc_provider_url"
}

data "aws_ssm_parameter" "database_address" {
  name = "${local.parametros}/database/address"
}
