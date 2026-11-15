# Transporte Parquet para PostgreSQL

O ArquivoLoader integra a solução e transporta lotes concluídos ao schema `arquivo`, sem cálculo de indicadores. Fluxo: `register-run --run-dir DIR`, `load --run-dir DIR` e `verify-data --run-dir DIR`, usando a configuração passada por `--config`. A API é um caminho independente.

## SQL recebido

| Arquivos | Papel |
|---|---|
| 00 e 01 | Papel, schema, cadastros e registro. |
| 02 | Versionamento das definições no banco. |
| 10 | Inventário somente de leitura; estimativas físicas não equivalem a count exato. |
| 12 | Preparo/checkpoint e finalização; exige schema vazio e banco `dados_publicos`. |
| 13 | Particionamento por lista, anexação e substituição transacional. |
| 15 | Coordenação entre preparo, finalização e autovacuum. |
| 16 | Reduz passadas na finalização e fornece reativação do autovacuum. |

Sequência candidata de instalação nova: 00 → 01 → 02 → 12 → 13 → 15 → 16. **Ainda precisa de validação em banco descartável**, incluindo autenticação/permissões. Não aplicar indiscriminadamente em banco carregado: o script 12 exige estado vazio. O papel inicial é NOLOGIN; configurar autenticação do ambiente, com senha em arquivo externo protegido.

Os resets específicos da produção (11 e 14), feed NuGet e caches Python não foram incorporados. Não são pré-requisitos de instalação nova. As dependências são restauradas pelo NuGet.Config, com cache em `.local/nuget`.

## Carga e conferência

Copie e ajuste `arquivo-loader.example.json` em uma pasta de configuração local. Os comandos com `--run-dir` delimitam o lote por organization-run.json, publications-v3.json e releases/. O exemplo recebido conserva caminhos do ambiente anterior; não aponta automaticamente para a máquina do usuário.

`carregar-e-verificar.sh` é referência operacional: usa `/mnt/dados`, `sudo -u nifi` e variáveis LOADER/CONFIG. Seleciona o lote READY mais recente por função, registra, carrega e verifica. Registra sucessos em `dados/logs/verificacao-ok.txt` como `pasta|publicação`. Isso permite retomada, mas não é um relatório completo de conferência.

`inventario-fluxo.py` cruza catálogo, lotes e metadados. TRANSPORTADO significa status/checkpoint, não comparação integral. `verify-data` compara o conteúdo Parquet com PostgreSQL.

Depois das cargas massivas com a função do script 16, reativar o autovacuum das partições: `SELECT arquivo.reativar_autovacuum_particoes();`, usando conta autorizada. O script de carga recebido não faz essa etapa automaticamente.

O Loader usa synchronous_commit=off: linhas e checkpoint ficam na mesma transação, mas uma falha pode exigir recarga de lotes recentes. A proteção do downloader/organizador não mede o disco remoto do banco nem limita seu WAL.

Datas/comentários dos SQLs são registros dos arquivos recebidos, não reconstrução do calendário acadêmico.
