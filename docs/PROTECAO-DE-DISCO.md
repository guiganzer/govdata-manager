# Proteção de espaço livre em disco

Descrição do código recebido em 15/11/2026. A referência de segurança é manter 20% do volume livre, mas as verificações variam por etapa.

| Etapa | Verificação | Efeito |
|---|---|---|
| Downloads | Espaço disponível atual menor ou igual a `total / 5`; monitor periódico e consultas durante a transferência | Bloqueia/pausa os trabalhos e cancela transferências em andamento conforme o controle de downloads. Não reserva previamente o tamanho completo de cada arquivo. |
| Captura da grade | `disponível - bytesPlanejados <= total / 5` nos pontos que recebem estimativa; outros pontos verificam apenas espaço atual | Interrompe a captura quando o teste falha. |
| Materialização de segmentos compatíveis com a API | Desconta buffers pendentes de dados e índice antes de gravar | Interrompe a materialização quando a margem é insuficiente. Essa etapa não é exigida no modo atual de organização somente em Parquet. |
| Organização final em Parquet | Antes da operação, desconta uma estimativa de duas vezes o tamanho da grade de entrada | Recusa a organização se a margem estimada não for suficiente. Não corresponde a monitoramento contínuo de todas as escritas internas do DuckDB. |

O volume é selecionado pela pasta de destino. No Linux, o código resolve caminhos e procura o ponto de montagem correspondente; no Windows, usa a unidade correspondente.

O limiar inclui igualdade: exatamente 20% já dispara a condição de proteção. Intervalos entre consultas, estimativas e outros processos que escrevem no volume podem produzir ultrapassagem transitória. Não há orçamento global que reserve antecipadamente toda a escrita de downloads, DuckDB e processos externos.

A retomada depende dos mecanismos de cada etapa: checkpoints/recibos dos downloads, estado das publicações e pontos de carga do Loader. Uma interrupção não equivale a concluir a operação nem garante preservar todo trabalho ainda não confirmado. A proteção descrita não controla automaticamente o disco remoto do PostgreSQL ou o crescimento do seu WAL.

Referências no código: `Core/StorageProtection.cs`, `Core/DownloadManager.cs`, `Server/RawGrid.cs`, `Server/Materializer.cs` e `Server/ConjuntoOrganizer.cs`, nos respectivos projetos `GovData.Downloads.*`.
