# API de leitura dos lotes organizados

`GovData.Downloads.Api` disponibiliza os lotes Parquet concluídos pelo organizador atual. Reaproveita cursores HMAC, política de rede, monitoramento e proteção de segredos do servidor anterior. O Worker mantém downloads e TUI; o ArquivoLoader continua transportando Parquet diretamente ao PostgreSQL.

## Configuração e execução

Prepare o [SDK local](../eng/AMBIENTE-LOCAL.md). Copie `eng/api/api.example.json` para `dados/api.json` e ajuste os caminhos. Caminhos relativos são resolvidos a partir da pasta do arquivo de configuração. Substitua `organization-output/orgao/funcao` por um ou mais lotes reais.

Cada lote precisa conter `organization-run.json` com `READY`, `publications-v3.json`, `publication-state.owner` e a árvore `releases/`. Apenas os IDs listados no lote são servidos. A abertura confere listas, caminhos, colunas e contagens dos metadados Parquet; isso não substitui a verificação integral de conteúdo do Loader.

No Windows, na raiz do projeto:

```powershell
$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.local/dotnet-home'
$env:NUGET_PACKAGES = Join-Path (Get-Location) '.local/nuget/packages'
& ./.dotnet/dotnet.exe build GovData.Downloads.Api/GovData.Downloads.Api.csproj -c Release
& ./.dotnet/dotnet.exe GovData.Downloads.Api/bin/Release/net10.0/GovData.Downloads.Api.dll init --config dados/api.json
& ./.dotnet/dotnet.exe GovData.Downloads.Api/bin/Release/net10.0/GovData.Downloads.Api.dll serve --config dados/api.json
```

No Linux:

```bash
export DOTNET_CLI_HOME="$PWD/.local/dotnet-home"
export NUGET_PACKAGES="$PWD/.local/nuget/packages"
./.dotnet/dotnet build GovData.Downloads.Api/GovData.Downloads.Api.csproj -c Release
./.dotnet/dotnet GovData.Downloads.Api/bin/Release/net10.0/GovData.Downloads.Api.dll init --config dados/api.json
./.dotnet/dotnet GovData.Downloads.Api/bin/Release/net10.0/GovData.Downloads.Api.dll serve --config dados/api.json
```

`init` cria ou valida chaves locais com permissões restritas. Não altera lotes, baixa dados ou imprime o token. `serve` também prepara as chaves se necessário. SDK, caches, dados e segredos ficam fora do Git.

Por padrão, escuta apenas `127.0.0.1:5080`. Para leitura na LAN, configure os dois IPv4 privados: endereço do servidor e endereço do único cliente permitido. A política recuperada recusa outros clientes, hosts e rotas administrativas pela LAN. HTTP deve permanecer em rede controlada; para atravessar redes não confiáveis, use túnel autenticado ou TLS apropriado.

## Rotas

| Método/rota | Uso |
|---|---|
| `GET /api/v1/saude` | Estado do serviço e versão 2 do envelope. |
| `GET /api/v1/publicacoes` | Publicações dos lotes, partições, contagens e hash de origem. |
| `GET /api/v1/fontes?offset=0&limit=100` | Catálogo e situação das fontes; filtro opcional `status`. |
| `GET /api/v1/documentos` | Referências aos PDFs do catálogo. |
| `GET /api/v1/conjuntos?offset=0&limit=100` | Conjuntos e contagens das partições disponíveis nos lotes. |
| `GET /api/v1/conjuntos/{id}/particoes` | Aceita `publicacao`, `offset` e `limit`. Sem publicação, seleciona a edição mais recente de cada partição. |
| `GET /api/v1/conjuntos/{id}/registros?publicacao=HASH&particao=ID&limit=1000` | Página JSON; repetir os parâmetros e acrescentar `cursor` para continuar. |
| `GET /api/v1/conjuntos/{id}/parquet?publicacao=HASH&particao=ID` | Parquet com suporte a HTTP Range. |
| `GET /api/v1/admin/atividade` | Monitor local, exige `Authorization: Bearer TOKEN`, usando `adminToken` do arquivo de segredos. |

A página mantém `conjunto`, `particao`, `versao`, `inicio`, `limit`, `quantidade`, `total`, `proximoCursor` e `registros`. Valores tabulares são strings ou nulos, incluindo `_linha` como string, como no leitor legado. `versao`/`X-Versao` identificam o hash da origem, não o hash do Parquet. `X-Publicacao` identifica a publicação e `X-Total` sua contagem de linhas.

Máximo de 50 mil registros por página, sujeito também ao teto de bytes. Sempre seguir `proximoCursor` até nulo. Cursores pertencem ao conjunto, partição, publicação e limite; mudar qualquer um invalida a continuação. Uma linha maior que o orçamento não é omitida silenciosamente: a requisição falha. `503` por leitores ocupados inclui `Retry-After`.

OpenAPI: `api.openapi.json`, nesta pasta. O frontend administrativo antigo e as rotas de modificação/expurgo não fazem parte deste host de leitura; downloads continuam na TUI/CLI. O catálogo é uma fotografia carregada na inicialização.

## Concorrência e recursos

A API mantém leitura compartilhada do arquivo de propriedade do lote. Isso impede reexecução do organizador no mesmo diretório enquanto o lote está servido. Gere versões novas em diretórios novos; atualize a configuração e reinicie a API para incluir/retirar lotes. Não substitua ou remova manualmente Parquets em serviço. O Loader pode ler os mesmos lotes; a API não acessa PostgreSQL nem inicia outro gerenciador de downloads.

O leitor usa uma faixa de `file_row_number`, ordenada pela posição física, e verifica `_linha`. Não gera segmentos JSON auxiliares. Cada leitor usa uma conexão DuckDB com uma thread, limite de memória e spill desativado. `maxReaders` multiplica o limite, além da memória das respostas/processo. O timeout solicita cancelamento da consulta.

O ensaio local com dez mil linhas verificou páginas limitadas por bytes, ordem, nulos, Unicode e última página. O plano analisado mostrou o filtro dentro de `READ_PARQUET`. Isso não é benchmark dos bilhões de linhas em produção: medir latência/memória em dados representativos antes da implantação. Para transferência massiva, permanecem Parquet e Loader direto.

## Verificação

```powershell
& ./.dotnet/dotnet.exe run --project GovData.Downloads.Api.Tests -c Release
```

Usa dados sintéticos sob `.local/api-tests` e porta temporária de loopback, sem acessar fontes governamentais ou produção. Testa o motor atual gerando Parquet sem segmentos, paginação HTTP, cursores, limites, acesso, Range e bloqueio de escrita concorrente. Preserva a fixture para inspeção local.
