terraform {
  required_version = ">= 1.7.0"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 5.0"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
  }

  # Backend parcial: bucket, região, tabela de lock e criptografia chegam por -backend-config no job deploy.
  backend "s3" {
    key = "servicos/execucao/production.tfstate"
  }
}

provider "aws" {
  region = var.regiao

  default_tags {
    tags = {
      Projeto    = "autoreparos"
      Servico    = local.servico
      Ambiente   = var.ambiente
      Gerenciado = "terraform"
    }
  }
}
