# Coleta leve de evidências Parquet

Usa o manifesto `organization-run.json` de **um lote READY** e lê somente os rodapés dos primeiros três arquivos. Não percorre a árvore de dados, não acessa PostgreSQL/API, não calcula hashes e não lê linhas. Informa tamanho físico, quantidade de linhas e grupos, comparando com o manifesto. Não substitui `verify-data` nem comprova integridade do conteúdo.

Compile uma vez, preferencialmente fora do horário de carga, na raiz do projeto (SDK .NET 10):

```bash
./.dotnet/dotnet build eng/parquet-evidence/ParquetEvidence.csproj -c Release
```

Se usa SDK no PATH, substitua `./.dotnet/dotnet` por `dotnet`. O SDK permanece local e ignorado pelo Git.

Execute no Linux, substituindo o diretório pelo lote do PNI já conhecido:

```bash
bash eng/parquet-evidence/coletar.sh /caminho/do/lote-pni > evidencia-parquet-pni.json
```

Por padrão são três arquivos, uma thread e limite de memória DuckDB de 128 MB. Cada consulta solicita cancelamento após dez segundos; isso não garante interromper imediatamente um disco/sistema de arquivos bloqueado. A preparação/compilação não entra nesse limite. Use lote local estável, sem reorganização concorrente. Não há bloqueio do produtor; uma alteração detectável no arquivo invalida a leitura.

O segundo argumento aumenta explicitamente o limite, até 100. Não é necessário ampliar agora: a contagem PNI já consta nos extratos recebidos. `linhas_rodapes_lidos` representa apenas os arquivos inspecionados; `linhas_declaradas_manifesto` continua sendo o total declarado. Não extrapole a amostra para comprovar todas as linhas.

Código de saída: 0 sucesso da coleta (que pode ser parcial); 1 erro/divergência; 2 argumentos inválidos. Preserve o JSON mesmo em caso de erro. Para executar no Windows, use `dotnet eng/parquet-evidence/bin/Release/net10.0/ParquetEvidence.dll DIRETORIO_DO_LOTE`.
