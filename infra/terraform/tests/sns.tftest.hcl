# C19 de infra-ci-observabilidade (D-67): o tópico autoreparos-execucao-eventos e a permissão de publicar nele.
# Roda sem AWS: provider aws simulado e parâmetros SSM da plataforma substituídos por valores de exemplo.

mock_provider "aws" {
  mock_data "aws_caller_identity" {
    defaults = {
      account_id = "198903849859"
    }
  }
  mock_resource "aws_sns_topic" {
    defaults = {
      arn = "arn:aws:sns:us-east-1:198903849859:autoreparos-execucao-eventos"
    }
  }
}

override_data {
  target = data.aws_ssm_parameter.oidc_provider_arn
  values = {
    insecure_value = "arn:aws:iam::198903849859:oidc-provider/oidc.eks.us-east-1.amazonaws.com/id/EXEMPLO"
  }
}

override_data {
  target = data.aws_ssm_parameter.oidc_provider_url
  values = {
    insecure_value = "oidc.eks.us-east-1.amazonaws.com/id/EXEMPLO"
  }
}

override_data {
  target = data.aws_ssm_parameter.database_address
  values = {
    insecure_value = "autoreparos-db.exemplo.us-east-1.rds.amazonaws.com"
  }
}

run "topico_da_execucao" {
  command = apply

  assert {
    condition     = aws_sns_topic.eventos.name == "autoreparos-execucao-eventos"
    error_message = "O tópico dos eventos da Execução precisa se chamar autoreparos-execucao-eventos."
  }

  assert {
    condition     = aws_sns_topic.eventos.kms_master_key_id == "alias/aws/sns"
    error_message = "O tópico precisa de criptografia em repouso com a chave gerenciada do SNS."
  }

  assert {
    condition = anytrue([
      for s in jsondecode(aws_iam_role_policy.irsa.policy).Statement :
      s.Effect == "Allow" && s.Action == "sns:Publish" && s.Resource == "arn:aws:sns:us-east-1:198903849859:autoreparos-execucao-eventos"
    ])
    error_message = "A IRSA da Execução precisa de sns:Publish só no tópico autoreparos-execucao-eventos."
  }

  assert {
    condition = alltrue([
      for s in jsondecode(aws_iam_role_policy.irsa.policy).Statement :
      !can(regex("sns:", jsonencode(s.Action))) || s.Resource == "arn:aws:sns:us-east-1:198903849859:autoreparos-execucao-eventos"
    ])
    error_message = "Nenhuma permissão de SNS da IRSA pode valer fora do tópico da Execução."
  }

  assert {
    condition     = output.topico_eventos_arn == "arn:aws:sns:us-east-1:198903849859:autoreparos-execucao-eventos"
    error_message = "O output topico_eventos_arn precisa expor o ARN do tópico."
  }
}
