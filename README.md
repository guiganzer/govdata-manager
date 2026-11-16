# GovData Manager

Baixe arquivos de fontes públicas, organize os dados em Parquet e disponibilize-os para outros sistemas. O GovData Manager reúne um assistente gráfico para Windows e ferramentas de automação para Windows e Linux.

O programa cuida da coleta e da organização inicial dos dados. O cálculo de indicadores e as transformações das camadas prata e ouro ficam a cargo dos sistemas consumidores.

## Comece pelo aplicativo Windows

1. Acesse a [página de releases](https://github.com/guiganzer/govdata-manager/releases/latest) e baixe **GovData-Desktop-Windows-x64-v0.2.0.zip**.
2. Extraia **todo o conteúdo** para uma pasta no seu computador.
3. Abra **GovData.Downloads.Desktop.exe** dentro da pasta extraída.

O pacote é para **Windows x64, Windows 10 1809 ou superior**. Inclui os componentes necessários: você não precisa instalar o SDK .NET. Mantenha as DLLs e subpastas junto do executável. O aplicativo ainda não possui assinatura digital comercial.

### Seu primeiro download

Selecione a fonte pronta **IBGE — Municípios do Censo 2022**, avance e escolha onde salvar os arquivos. Revise as informações e clique em **Baixar e organizar**.

No teste desta versão, o download de aproximadamente **326 KB** gerou **5.570 linhas em Parquet**. Ao concluir, o aplicativo mostra uma prévia e permite abrir a pasta dos resultados. O arquivo é obtido diretamente do IBGE; sua disponibilidade depende do órgão.

### Cadastre uma fonte

Use **Cadastrar uma fonte CSV**. O assistente conduz você por quatro etapas: identificação, endereço do arquivo, configuração das colunas e revisão. Cada etapa oferece explicações no botão de ajuda. Salvar o cadastro não inicia o download: você confirma a execução depois.

O cadastro guiado aceita CSV com cabeçalho, endereço HTTPS direto, separador vírgula, ponto e vírgula ou tabulação, e codificação UTF-8 ou Windows-1252. É necessário informar os nomes das colunas. Arquivos e layouts mais complexos usam os contratos e as ferramentas avançadas do projeto.

[Consulte o guia do aplicativo](docs/DESKTOP-WINDOWS.md) para conhecer os limites, os arquivos gerados e as opções de cancelamento e retomada.

## Escolha como operar

| Necessidade | Ferramenta |
|---|---|
| Começar com uma fonte pequena ou cadastrar um CSV | Assistente Windows com etapas guiadas e ajuda |
| Manter downloads automáticos de várias fontes | [Worker e interface de terminal (TUI)](eng/download-worker/README.md) |
| Organizar arquivos com contratos e layouts específicos | [Organizador](eng/organizer/README.md) |
| Consumir lotes prontos por HTTP, JSON paginado ou Parquet | [API de leitura](docs/API.md) |
| Carregar grandes volumes diretamente no PostgreSQL | [ArquivoLoader e scripts SQL](eng/arquivo/README.md) |

O fluxo é: **catálogo → downloads → organização em Parquet → API ou carga no PostgreSQL**. Os lotes concluídos recebem o estado `READY`. PostgreSQL só é necessário para a carga direta; o assistente Windows funciona sem banco de dados externo.

O assistente se destina a arquivos pequenos e tem limite de dez minutos por execução. Para cargas extensas e operação contínua, utilize as ferramentas de terminal. Leia também a [proteção de disco por etapa](docs/PROTECAO-DE-DISCO.md).

## Uso e acessibilidade

A interface oferece linguagem simples, revisão antes de executar, ajuda contextual, estados escritos, navegação por teclado, controles com nomes semânticos, botões amplos e janela redimensionável com rolagem. Essas escolhas foram orientadas pelos sete Princípios do Desenho Universal.

A validação de uso e acessibilidade desta versão foi informada como concluída pelo responsável pelo projeto. Esse registro se refere ao teste realizado e não representa certificação independente.

## Para desenvolver e instalar os serviços

O código usa .NET 10. O SDK **10.0.401** fica localmente em `.dotnet/`, e os caches em `.local/`; nenhum deles é versionado. Consulte o [guia do ambiente local](eng/AMBIENTE-LOCAL.md).

Para preparar o ambiente e compilar o aplicativo Windows:

```powershell
./eng/setup-local.ps1
./eng/publish-desktop-windows.ps1
```

Para verificar o fluxo de organização e a API:

```powershell
./.dotnet/dotnet.exe run --project GovData.Downloads.Desktop.Flow.Tests -c Release
./.dotnet/dotnet.exe run --project GovData.Downloads.Api.Tests -c Release
```

O teste do fluxo usa uma fonte sintética. Acrescente `-- --live-ibge` ao primeiro comando para incluir o download público pequeno e conferir a contagem física no Parquet.

O motor, a API e as ferramentas de terminal atendem Windows e Linux; a interface MAUI é exclusiva para Windows. Os guias de cada componente descrevem os pré-requisitos e as configurações dos serviços. Ao atualizar um serviço, interrompa sua execução antes de substituir os binários.

## Licença

Código próprio sob [licença MIT](LICENSE), Copyright (c) 2026 Guilherme Ceolin Ganzer. As dependências mantêm seus próprios termos, disponíveis em [avisos de terceiros](THIRD_PARTY_NOTICES.md) e no [inventário do desktop](docs/DEPENDENCIAS-DESKTOP.md).

Dados baixados, credenciais, logs de produção, SDK e caches não fazem parte do repositório público.
