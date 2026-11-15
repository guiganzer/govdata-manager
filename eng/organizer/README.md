# Organização manual por órgão

O comando `govdata-organizer` usa o motor V3 existente para ler os downloads
concluídos, validar recibos e organizar Parquets. Não abre API nem cadastra
publicações no PostgreSQL. O worker de Downloads pode continuar ativo; arquivos
que mudarem durante a cópia são rejeitados pelo motor.

O arquivo `dados/organizer.json` guarda os padrões da execução. Exemplo:

```json
{
  "schemaVersion": 1,
  "organizationId": null,
  "functionId": null,
  "outputRoot": "organization-output",
  "requireAllFiles": true,
  "force": false,
  "progressIntervalSeconds": 5,
  "conjuntoPatterns": ["*"]
}
```

`outputRoot` relativo é resolvido dentro de `dados`. Sem `--output`, o órgão
usa `dados/<outputRoot>/<organizationId>` e a função usa
`dados/<outputRoot>/<organizationId>/<functionId>`. `--output /caminho`
substitui essa pasta. `--organization-id` e `--function-id` substituem seus
valores na configuração; `--force` substitui `force`. `--config arquivo.json`
carrega outro perfil. A configuração de lote,
threads e memória DuckDB continua em `dados/server/settings.json`.
`progressIntervalSeconds` define a frequência das mensagens durante `organize`
(1 a 60 segundos, padrão 5). `--progress-interval-seconds N` altera apenas
a execução atual. A organização roda em primeiro plano; o progresso sai em
`stderr`, enquanto o plano e o manifesto final permanecem em `stdout`.
O início da execução informa o limite efetivo de memória DuckDB, o número de
threads e o lote de captura. Uma falha anterior com os mesmos arquivos e
parâmetros não é repetida automaticamente. Alterar memória, threads ou lote
habilita uma nova tentativa; `--force` solicita outra tentativa com os mesmos
parâmetros.

## Operação

```bash
cd /mnt/dados/govdata-manager
./govdata-organizer plan --organization-id ID_DO_ORGAO
./govdata-organizer organize --organization-id ID_DO_ORGAO
./govdata-organizer organize --organization-id ID_DO_ORGAO --output /mnt/dados/govdata-manager/dados/organization-output/ID_DO_ORGAO
./govdata-organizer list
./govdata-organizer list --organization-id tesouro-nacional
./govdata-organizer plan --organization-id tesouro-nacional --function-id tesouro-capag-municipios
./govdata-organizer organize --organization-id tesouro-nacional --function-id tesouro-capag-municipios
```

`plan` não cria publicação. Imprime `sourceFiles`, `definitions`, `documents`,
`unmapped`, `missingDownloads`, `crossScopeDefinitions` e `ready`. Exit code 0
indica pronto; 3 indica bloqueio de pré-validação. Em modo padrão, `organize`
só roda se todos os arquivos do escopo têm recibo atual e todos os arquivos
tabulares têm definição V3. PDFs são documentos de apoio: exigem download
válido, são copiados para `documents/` na saída e entram no manifesto com
SHA-256; não viram Parquet. A opção consciente
`--allow-unmapped` permite apenas os tabulares cobertos por V3; o relatório
registra os que ficaram fora.

`list` apresenta o catálogo em árvore órgão → função, com contagem de downloads
válidos, tabulares cobertos por V3 e PDFs de apoio. A marca `PRONTA` requer
downloads completos e layout para todos os tabulares. Processar uma função
nunca seleciona outras funções do mesmo órgão. Uma definição que misture
funções é bloqueada no plano.

O motor só trabalha no órgão selecionado. Se uma definição misturar fontes de
dois órgãos, o plano marca `crossAgencyDefinitions` e bloqueia a execução.
Repetir o comando reutiliza publicações idênticas na mesma pasta; `--force`
permite nova tentativa após falha, sem forçar edição duplicada para entradas
iguais. Não use a pasta legada `dados/server/storage` como `--output`.

Após sucesso, a pasta contém `publications-v3.json`, `releases/`, `audit.jsonl`
e `organization-run.json` com status `READY`, IDs das publicações, partições,
linhas, hashes e caminhos relativos dos Parquets. É a entrada planejada para a
etapa manual de transporte, que será implementada separadamente. O Bronze não
é alterado por estes comandos.

## Implantação

Envie `dist/govdata-organizer-src.zip` para `/mnt/dados/staging` e extraia sobre
`/mnt/dados/govdata-migracao`. Preserve `govdata-manager-v2/dados` inteiro. Rode
`bash eng/publish-organizer-linux.sh` e `bash eng/install-organizer-manual.sh`.
Não há serviço systemd a iniciar para esta fase.

## Limite de memória DuckDB

A organização manual usa `duckDbMemoryMb` e `duckDbThreads` de
`dados/organizer.json` quando definidos; caso contrário, usa os valores de
`dados/server/settings.json`. Os argumentos `--duckdb-memory-mb` e
`--duckdb-threads` substituem ambos apenas na execução atual. O limite em MiB
pode ser de 512 a 65536; mantenha `duckDbThreads` em 1 para controlar o pico.
`captureBatchRows` em `organizer.json` ou `--capture-batch-rows N` define o
tamanho dos blocos da grade larga (padrão: 4096 linhas). Reduza para 1024 ou
512 se uma captura real exceder o limite de memória. Um lote maior produz
menos arquivos e reduz o custo de leitura na organização.
`--recapture-grid` refaz a grade já armazenada com o tamanho de bloco atual.
Use somente para uma função cuja organização falhou ou ainda não terminou;
o padrão reutiliza a grade. A substituição ocorre depois de a nova captura
terminar, e o original baixado permanece intacto.

Se o DuckDB parar perto de 1 GiB com `Out of Memory Error`, confira a memória
livre e o limite do cgroup antes de aumentar o teto. Comece com 1536 MiB:

```bash
free -h
cat /sys/fs/cgroup/memory.max 2>/dev/null || true
./govdata-organizer organize --organization-id tesouro-nacional \
  --function-id tesouro-capag-municipios \
  --duckdb-memory-mb 1536 --duckdb-threads 1 --force
```

`--force` libera a nova tentativa da definição que falhou com a mesma entrada.
O organizador reutiliza publicações já concluídas na pasta de saída. A pasta
`.duckdb-spill` fica no próprio volume da saída; deixe espaço livre ali.
A captura grava a grade larga em blocos Parquet separados e informa cada bloco
concluído no terminal. O processamento seguinte lê os blocos como uma grade.
Não aumente o limite apenas para contornar uma captura longa; confira a etapa
exata da falha e a memória disponível para PostgreSQL, NiFi e Downloads.

## Modo padrão e modo massivo

A captura escolhe o leitor pelo volume de cada arquivo:

| Volume (CSV descompactado) | Modo | Leitura |
| --- | --- | --- |
| abaixo de `massiveCsvThresholdMb` (padrão 256 MiB), planilhas, DBF/DBC | padrão | leitor linha a linha em .NET, blocos Parquet pequenos |
| a partir do limite, CSV solto ou membros CSV de ZIP | massivo | membro convertido em fluxo para UTF-8 e lido pelo parser nativo do DuckDB, um Parquet por membro |

No modo massivo o membro do ZIP é extraído temporariamente para a pasta de
captura (exige espaço livre equivalente ao membro descompactado; a regra dos
20% livres continua valendo) e apagado logo após a leitura. `_linha` é o número
do registro no CSV; difere da linha física apenas em campos com quebra de linha
entre aspas. Um ZIP com algum membro não CSV fica no modo padrão.

`massiveCsvThresholdMb` fica em `organizer.json` ou `server/settings.json`;
`--massive-threshold-mb N` altera só a execução atual e `0` desativa o modo.
Os dois modos geram o mesmo `organized.parquet` (conferido em 1 milhão de
linhas no layout de Estabelecimentos da RFB: 44,3 s → 9,2 s por arquivo).
No modo massivo o CSV vai do `read_csv` direto para o Parquet, numerado em
fluxo; não há tabela intermediária. Medido: memória acima de 1536 MiB e mais
de uma thread DuckDB não reduzem o tempo (a numeração em fluxo é sequencial).

Na organização via CLI, o resultado vai direto da grade para o Parquet, sem
tabela intermediária no DuckDB. Contagens, nulos obrigatórios e a sequência de
`_linha` são conferidos no próprio Parquet em uma única varredura. Os grupos de
linhas Parquet passam a ter cerca de 1 milhão de células (tabelas estreitas,
como as da RFB, ficam com ~32 mil linhas por grupo; largas mantêm ~2 mil).
