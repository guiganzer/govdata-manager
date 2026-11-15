# GovData Manager

Gerencia fontes de dados públicos, downloads autônomos, organização em Parquet e disponibilização por API ou carga direta no PostgreSQL. Catálogo e contratos configuram cada fonte. Cálculo de indicadores, prata/ouro e aplicativo consumidor são externos ao software.

## Componentes

| Componente | Responsabilidade |
|---|---|
| `GovData.Downloads.Core` | Catálogo, downloads, recibos, fila, limites e retomada. |
| `GovData.Downloads.Worker.Cli` | Serviço autônomo de downloads. |
| `eng/download-worker` | TUI Textual para acompanhar/controlar o Worker. |
| `GovData.Downloads.Organizer.Cli` | Planejamento e organização por órgão/função em lotes Parquet. |
| `GovData.Downloads.Server` | Biblioteca compartilhada do motor de organização. |
| `GovData.Downloads.Api` | HTTP de leitura de lotes READY, JSON paginado e Parquet. |
| `GovData.Downloads.ArquivoLoader` | Transporte PostgreSQL via COPY, checkpoints e verificação. |
| `GovData.Downloads.CatalogTool` | Validação dos contratos. |
| `eng/arquivo` | SQL e exemplos do transporte ao bronze. |

## Preparação e build

O SDK .NET 10.0.401 é **local**, em `.dotnet/`; caches ficam em `.local/`. Ambos são ignorados pelo Git. Veja [ambiente local](eng/AMBIENTE-LOCAL.md).

Na raiz do projeto, após preparar o ambiente:

```powershell
& ./eng/setup-local.ps1
& ./.dotnet/dotnet.exe build GovData.Downloads.Worker.Cli/GovData.Downloads.Worker.Cli.csproj -c Release
& ./.dotnet/dotnet.exe build GovData.Downloads.Organizer.Cli/GovData.Downloads.Organizer.Cli.csproj -c Release
& ./.dotnet/dotnet.exe build GovData.Downloads.ArquivoLoader/GovData.Downloads.ArquivoLoader.csproj -c Release
& ./.dotnet/dotnet.exe build GovData.Downloads.Api/GovData.Downloads.Api.csproj -c Release
& ./.dotnet/dotnet.exe run --project GovData.Downloads.Api.Tests -c Release
```

## Operação

- [API e contrato HTTP](docs/API.md).
- [TUI e downloads](eng/download-worker/README.md).
- [Organizador](eng/organizer/README.md).
- [SQL e carga PostgreSQL](eng/arquivo/README.md).
- [Proteção de disco por etapa](docs/PROTECAO-DE-DISCO.md).

Fluxo: catálogo → downloads → lotes Parquet READY → API/consumidor ou ArquivoLoader → bronze. O Loader é parte da solução, não uma etapa ainda por implementar. PostgreSQL é necessário apenas para esse transporte direto.

Os scripts Linux recuperados ainda pressupõem caminhos/usuários do ambiente original. Não são instalação genérica em máquina vazia; leia os pré-requisitos. A API nova é independente desses instaladores e tem instruções próprias. Não sobrescreva executáveis em execução: prepare nova pasta de versão e faça parada/troca/início controlados.

## Situação da distribuição

Código próprio sob licença MIT, Copyright (c) 2026 Guilherme Ceolin Ganzer. Consulte `LICENSE` e `THIRD_PARTY_NOTICES.md`; os termos das dependências são preservados. Publicação e versão de release ainda serão preparadas. Entrevistas, relatório acadêmico, dados, logs de produção, credenciais e SDK não pertencem ao repositório público.

As regras históricas em `REGRAS-DE-NEGOCIO.md` devem ser lidas junto da atualização de escopo: TUI e API são componentes separados; não há host HTTP embutido no Worker nem reprocessamento automático pela API.
