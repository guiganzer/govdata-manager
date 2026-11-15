# RFB CNPJ: organizacao dos arquivos baixados

Os ZIPs de Estabelecimentos de 2026-04 a 2026-10 tem dez fragmentos por mes.
O membro tabular termina em `.ESTABELE`, embora contenha CSV sem cabecalho,
delimitado por `;`, com 30 campos. O contrato V3 usa `fontes.membros` para
selecionar o membro e `fontes.formatoMembro: "csv"` para declarar seu formato.
O leitor e generico para outros arquivos com o mesmo problema de extensao.

Cada mes de Estabelecimentos e uma publicacao `LISTA` com dez particoes. Cada
mes de Municipios e uma publicacao separada. O contrato existente
`rfb-cnpj.municipios` cobre 2026-10. Sao 17 definicoes para as 80 fontes da
funcao: sete Estabelecimentos, sete Municipios e tres tabelas auxiliares.

Os nomes das 30 colunas seguem o documento oficial da Receita Federal
`https://www.gov.br/receitafederal/dados/cnpj-metadados.pdf`. Os valores
permanecem como texto no Parquet. O arquivo `zip-inventario.tsv` confirmou 30
campos nos 70 fragmentos amostrados e dois campos em Municipios. A codificacao
dos Estabelecimentos e tratada como Windows-1252; a amostra de 64 KiB de cada
ZIP nao prova a codificacao de todos os registros, portanto o primeiro mes
deve ser conferido antes do lote completo.

Reprocessar em outra pasta de saida evita reutilizar publicacoes anteriores.
O comando `--force` na mesma pasta repete tentativas falhas, mas uma publicacao
imutavel de entrada identica nao e sobrescrita. Guarde a saida anterior ate
conferir os novos Parquets e o manifesto `organization-run.json`.

Antes de iniciar uma carga no Bronze, confira a politica de tabelas por
competencia. Cada conjunto mensal tem seu proprio nome e tabela de destino.
