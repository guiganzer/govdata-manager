# Contrato de catálogo v2

Esta é a especificação para a interface do aplicativo e para outro modelo que cadastre opções de download. O catálogo ativo fica em `.local/app/catalogo.json`; a definição estrutural verificável está em `docs/catalogo-v2.schema.json`. O aplicativo aceita somente a versão 2. O catálogo começa vazio nesta versão.

## Estrutura e exemplo

```json
{
  "versao": 2,
  "orgaos": [
    {
      "id": "datasus",
      "nome": "DATASUS",
      "funcoes": [
        {
          "id": "datasus-pni",
          "nome": "PNI",
          "arquivos": [
            {
              "id": "pni-vacinacao-2026-01",
              "nome": "Vacinação janeiro/2026",
              "url": "https://s3.sa-east-1.amazonaws.com/ckan.saude.gov.br/PNI/json/vacinacao_jan_2026_json.zip",
              "nomeArquivo": "vacinacao_jan_2026_json.zip",
              "competencia": "2026-01",
              "substituivel": false,
              "paginaOrigem": "https://example.org/pni"
            }
          ]
        }
      ]
    }
  ]
}
```

O exemplo ilustra o formato, sem cadastrar nem afirmar a disponibilidade atual da fonte. O endereço `example.org` é apenas ilustrativo; confirme a página específica da fonte antes de usar o registro.

## Campos

| Nível | Campo | Obrigatório | Regra |
| --- | --- | --- | --- |
| Catálogo | `versao` | Sim | Inteiro `2`. |
| Catálogo | `orgaos` | Sim | Lista; pode estar vazia. |
| Órgão | `id`, `nome`, `funcoes` | Sim | Identificador estável, nome legível e lista de funções. |
| Função | `id`, `nome`, `arquivos` | Sim | Pertence a um único órgão. |
| Arquivo | `id`, `nome`, `url`, `nomeArquivo`, `substituivel` | Sim | Identidade, exibição, endereço direto, nome local e política de atualização. |
| Arquivo | `competencia` | Não | Período mensal `AAAA-MM`, mês de `01` a `12`; use em fontes mensais. |
| Arquivo | `frequenciaVerificacao` | Não | Apenas para `substituivel: true`: `diaria`, `mensal` ou `anual`. Omitido: mensal se houver `competencia`; diária caso contrário. |
| Arquivo | `paginaOrigem` | Não | Página pública HTTP(S) de procedência, distinta da URL direta do arquivo. |
| Arquivo | `descricao` | Não | Contexto operacional, até 1.000 caracteres. |
| Arquivo | `tamanhoEsperadoBytes` | Não | Inteiro não negativo; tamanho exato para conferência. |
| Arquivo | `sha256` | Não | Hash hexadecimal de 64 caracteres para conferência. |

`substituivel: true` significa que o mesmo endereço pode entregar uma versão nova do arquivo, como um conjunto preliminar ou atualizado diariamente. Nesse caso, **não informe** `tamanhoEsperadoBytes` nem `sha256` fixos; o schema e o validador rejeitam essa combinação. `frequenciaVerificacao` define quando o aplicativo volta a consultar essa URL após a última verificação concluída: 24 horas, um mês calendário ou um ano calendário. Sem o campo, a competência define o padrão mensal; sem competência, o padrão é diário. `false` indica versão fixa: depois de baixada e conferida, só é baixada novamente se o arquivo local desaparecer, mudar ou o registro do catálogo mudar. O operador também pode iniciar outro download manualmente. Fontes fixas não aceitam `frequenciaVerificacao`.

## Identidade, período e destino

1. `id` é único em todo o catálogo, inclusive entre tipos diferentes, e não deve mudar após o cadastro. Use 1 a 80 caracteres: letras minúsculas ASCII, dígitos, ponto, hífen e sublinhado; comece por letra ou dígito. A fila guarda esse ID.
2. Para uma fonte mensal, crie um registro por competência. Preencha `competencia` e inclua o período no `id` e no `nome` legível. Use um novo ID para cada mês.
3. `nomeArquivo` deve ser único, sem distinção entre maiúsculas e minúsculas, **dentro da mesma função**. Para nomes repetidos por competência, acrescente o período ao nome local, por exemplo `2026-10_Estabelecimentos0.zip`.
4. O destino é `<pasta de downloads>/<órgão>/<função>/<nomeArquivo>`. O aplicativo valida também colisões de destino entre funções/órgãos que resultem no mesmo caminho local.
5. `nomeArquivo` não pode conter separadores, caracteres proibidos pelo Windows, espaços ou pontos finais, nem nomes reservados como `CON` e `NUL`.

## URLs e operação

1. `url` aponta **diretamente** para um arquivo público em `https://` ou `ftp://`; não use autenticação embutida, fragmento (`#`) ou caminho de diretório. `paginaOrigem`, quando conhecida, é uma página HTTP(S) pública de documentação ou publicação. Não invente a página: omita o campo se não conseguir confirmá-la.
2. No perfil automático, ao abrir, o aplicativo confere o catálogo com os arquivos locais e os comprovantes em `downloads-receipts.json`. Fontes ausentes, alteradas ou sem comprovante entram automaticamente na fila. O aplicativo repete a conferência a cada 30 segundos enquanto estiver aberto e após salvar o catálogo; também há o comando **Verificar agora**. O limite de downloads paralelos fica em `settings.json`, configurável de 1 a 16 (padrão: 3) pela interface. O controle **Pausar automação** interrompe os jobs automáticos e impede novos agendamentos até a retomada; downloads iniciados manualmente continuam. No perfil manual, a conferência e as novas tentativas automáticas ficam desabilitadas; o contrato das fontes permanece o mesmo.
3. Em cada transferência, o sistema grava um arquivo `.part`, verifica tamanho/hash quando fornecidos e só então substitui o arquivo final. Uma falha preserva a versão final anterior. O comprovante é gravado após a verificação; limpar o histórico da fila não apaga o comprovante.
4. Uma fonte substituível é revalidada após a frequência indicada no cadastro. Em HTTPS, ETag forte ou fraco permite `If-None-Match`; `Last-Modified` permite `If-Modified-Since` quando não há ETag. A resposta `304` atualiza o comprovante sem regravar o arquivo. Sem validador, ou em FTP, uma nova cópia completa é transferida quando o prazo vence. Um parcial só é retomado por HTTP quando existe ETag **forte** da mesma versão; em FTP ou sem esse identificador, o parcial é descartado e o download recomeça.
5. Em caso de handshake TLS interrompido antes de receber HTTP, o aplicativo abre até duas novas conexões em HTTP/1.1 no mesmo job; se todas falharem, registra o erro. Falhas transitórias de job são repetidas automaticamente após 1, 2, 4, 8, 16, 32 e até 60 minutos. Falhas HTTP 401, 403 e 404, de integridade ou de resposta inválida aguardam 24 horas. O último erro e o horário da próxima tentativa continuam visíveis mesmo quando o job volta à fila. Após três jobs com falha de conexão/TLS no mesmo servidor em dez minutos, novas transferências automáticas desse servidor aguardam 15 minutos; outros servidores continuam. A prioridade manual ignora essa espera e ocupa a próxima vaga livre, sem interromper transferências ativas.

## Regras para outro modelo

1. Produza JSON UTF-8 com nomes de propriedade exatamente em `camelCase`. Campos desconhecidos e duplicados são rejeitados.
2. Cadastre o órgão e a função corretos; agrupe registros da mesma origem. Não replique o órgão só porque a competência muda.
3. Confirme a URL direta e a página de origem antes de preencher. Escolha explicitamente `substituivel` para cada arquivo; use `true` quando o conteúdo puder mudar sem mudar a URL. Para essas fontes, preencha `frequenciaVerificacao` com base na cadência real de publicação, especialmente quando a fonte for mensal ou anual. A frequência é um intervalo mínimo de consulta, não uma garantia de publicação.
   Antes de ativar uma fonte nova, confira também se o esquema, host e prefixo da
   URL direta constam em `catalogos/origens-download.json` e no arquivo operacional
   `<GOVDATA_HOME>/server/download-origins.json`. A política é carregada na
   inicialização; reinicie o aplicativo depois de alterar a lista operacional.
4. Preserve IDs estáveis de registros que continuarem representando a mesma fonte. Ao adicionar outro mês, crie novo ID e nome local único.
5. Valide o arquivo candidato antes de substituir o catálogo ativo. A gravação pela interface é atômica e gera `.bak`; em edição externa, evite que o aplicativo leia um JSON parcialmente gravado. Um registro válido passa a ser agendado automaticamente após a recarga ou próxima conferência.

## Validação

Na raiz do projeto, com o SDK local:

```powershell
& .\eng\dotnet.ps1 run --project .\GovData.Downloads.CatalogTool --no-restore -- validate .\.local\app\catalogo.json
```

O comando retorna `0` para catálogo válido, `2` para inválido e `64` para uso incorreto. O schema cobre estrutura e tipos; o validador do aplicativo também verifica unicidade de IDs e nomes locais, URLs, caminhos e regras entre campos. Depois de uma edição externa, use **Recarregar JSON** no Catálogo.

## Compatibilidade

A v2 começa com `{"versao":2,"orgaos":[]}`. Arquivos v1 não são migrados nem aceitos pelo aplicativo v2, conforme a decisão de iniciar o catálogo do zero. O contrato v1 foi removido do projeto.

