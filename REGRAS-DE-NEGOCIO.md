# Regras de negócio — GovData Manager v2

## Escopo e fontes

- Ubuntu 24.04 via SSH; .NET 10 nos executáveis de processamento e Textual na TUI de Downloads.
- Catálogo de downloads exclusivamente v2. As regras específicas de fonte e layout ficam em JSON, nunca em ramos de código por endpoint.
- `dados/` é estado operacional: downloads, recibos, configurações ativas e Parquets. Não é incluído no código-fonte nem apagado durante implantação.
- O código de desenvolvimento fica na raiz deste projeto. `/mnt/dados/govdata-manager` é o caminho da implantação original, não requisito do motor. Configurações ativas permanecem externas ao código.

## Downloads

- Um serviço autônomo mantém a fila de todo o catálogo. A TUI é cliente local, abre sob demanda e atualiza a cada 5 segundos; fechá-la não para downloads.
- Somente um processo controla a fila e os recibos. Ações como pausar, retomar e verificar são confirmadas na TUI e enviadas por arquivos locais de controle, sem API HTTP.
- Um arquivo só é considerado concluído com recibo compatível com o catálogo e arquivo local. A automação não reinicia downloads íntegros a cada inicialização.
- O serviço não organiza Parquet e não cadastra dados no PostgreSQL.

## Organização manual

- `govdata-organizer list` apresenta órgão → função e conta downloads, layouts V3 e documentos.
- `plan` é pré-validação sem publicação. `organize` é uma chamada explícita por órgão ou por função. O argumento `--output` prevalece sobre o diretório padrão de `dados/organizer.json`.
- A execução exige todos os downloads do escopo com recibos atuais. Por padrão, exige definição V3 para cada arquivo tabular; `--allow-unmapped` permite uma exceção explícita e registra os arquivos excluídos.
- PDFs são documentos de apoio: exigem recibo, são copiados para a saída e recebem SHA-256 no manifesto. Não viram Parquet.
- Uma definição que misture fontes fora do órgão/função selecionado bloqueia a execução. Nenhum outro órgão/função é processado por consequência de um comando.
- A saída fica em pasta própria, com estado de publicações, Parquets e `organization-run.json` marcado `READY` somente quando todos os itens elegíveis terminam. Repetir com entradas iguais reutiliza a publicação; mudanças nos insumos são verificadas.
- Organizar não altera o schema `arquivo` nem dispara NiFi.

## Transporte para Bronze

- O transporte direto integra a solução e consome a pasta `READY` indicada, com validação de manifesto, caminhos, hashes, esquema e contagens antes de cadastrar.
- O schema PostgreSQL é `arquivo`; o Loader .NET 10 usa Parquet em fluxo e `COPY FROM STDIN` com checkpoints. O NiFi deve receber um lote explícito, sem selecionar pendências globais.
- A tabela visível representa a carga mais recente de cada partição sem eliminar as demais. Os SQLs recuperados implementam preparo, troca por partição e anexação; a sequência de instalação nova ainda precisa de teste em banco descartável.
- Não limpar Parquets, downloads, schema `arquivo` nem registros legados antes de uma carga v2 conferida.

## API de leitura

- `GovData.Downloads.Api` serve lotes `READY` explicitamente configurados, com JSON paginado e download Parquet. Não inicia downloads, não reorganiza fontes e não executa carga PostgreSQL.
- Enquanto serve um lote, mantém o bloqueio compartilhado compatível com a propriedade exclusiva usada pelo organizador. Para nova organização, usar outro diretório ou retirar o lote da API antes de reexecutar.
- O leitor atual não exige os segmentos JSON da API anterior. O Loader e o HTTP são caminhos independentes de consumo dos mesmos Parquets.

## Implantação

- A instalação original usa `govdata-downloads.service`, `/etc/govdata-manager` e `/opt/govdata-manager`. Não sobrescrever binários em execução; publicar em pasta de versão separada e fazer parada/troca/início controlados.
- O motor compartilhado conserva o nome `GovData.Downloads.Server`; o host novo fica em `GovData.Downloads.Api`.
- Git/CI e publicação dependem de revisão da distribuição, licença e avisos de terceiros. SDK, caches, dados e credenciais ficam fora do histórico.
