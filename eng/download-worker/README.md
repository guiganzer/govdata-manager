# GovData Downloads autônomo

Esta é a primeira fase do fluxo novo. O worker .NET 10 lê `dados/catalogo.json`
(contrato v2), `dados/settings.json`, a política de origens, a fila e os recibos.
Mantém os downloads em segundo plano no `systemd --user`. Não abre API, não
organiza Parquet e não escreve no PostgreSQL. Fechar a TUI não para o worker.

## Instalação no Ubuntu

O diretório de dados da v2, após migração conferida, será
`/mnt/dados/govdata-manager/dados`. Preserve-o inteiro. O pacote
de código não contém dados e pode ser extraído sobre a pasta `govdata-manager`
sem apagar o diretório `dados`.

1. Pare e desabilite os serviços antigos de Manager/Downloads, caso existam.
2. Extraia o pacote de código. Execute
   `python3 eng/download-worker/validar-dados.py dados` antes de ligar o worker.
3. Execute `bash eng/publish-downloads-linux.sh` e
   `bash eng/install-downloads-autonomo.sh`.
4. Abra `./govdata-downloads` sob demanda por SSH. A tela atualiza a cada
   5 segundos. F3 a F8 filtram; F9 busca; PgUp/PgDn navegam; F10 reconcilia;
   F11 pausa/retoma a automação; `p`/`r` atuam no job selecionado; F12 sai.

O instalador exige Python 3 com `venv`, acesso ao PyPI para instalar `textual`,
e SDK .NET 10 em `.dotnet` dentro desta pasta. Se o SDK local não existir, rode
`bash eng/setup-dotnet-linux.sh`. A publicação instala o worker autocontido em
`dist/linux-x64/download-worker`; não depende do .NET global depois do build.

O serviço usa apenas arquivos locais de controle em `dados/downloads-control`.
`run-agency --organization-id ID` continua disponível para uso manual somente
quando o serviço autônomo estiver parado, pois ambos controlam a mesma fila.

## Limites de downloads simultâneos

Três regras valem juntas, para não sobrecarregar os servidores do governo:

- `maxConcurrentDownloads` (1 a 16): total de downloads simultâneos.
- `maxDownloadsPorOrgao` (1 a 16, padrão 2): máximo simultâneo por órgão do catálogo.
- `limitesPorOrgao`: limite próprio de um órgão, pelo `id` do catálogo; vale no lugar do padrão.

Além disso, cada servidor de origem recebe no máximo um download por vez. O órgão de cada
arquivo vem do `catalogo.json`; um arquivo sem órgão conhecido é agrupado pelo servidor.
Exemplo de `dados/settings.json`:

```json
{
  "downloadRoot": "/mnt/dados/govdata-manager/dados/downloads",
  "maxConcurrentDownloads": 16,
  "maxDownloadsPorOrgao": 2,
  "limitesPorOrgao": { "datasus": 1, "receita-federal": 1 },
  "profile": { "automationEnabled": true }
}
```

Os limites são lidos ao iniciar o serviço e registrados no `eventos.jsonl` (`limits.loaded`).

## Estado e segurança

- `dados/downloads.json`: jobs e checkpoints de downloads.
- `dados/downloads-receipts.json`: recibos dos arquivos completos.
- `dados/downloads-service.json`: estado resumido do worker, para a TUI.
- `dados/downloads-control/inbox`: comandos locais escritos pela TUI.
- `dados/downloads-control/done`: resultados dos comandos.
- `dados/automation.json`: pausa/retomada persistente da automação.

O lock `dados/download-state.owner` impede dois controladores da fila. O
`MemoryMax` do serviço é 1536 MiB e `CPUQuota` é 150%; ajuste somente após medir
o uso real. Não há listener TCP nem dependência do NiFi.

`validar-dados.py` não altera arquivos: verifica contrato v2, recibos,
tamanhos e que os caminhos de downloads permanecem dentro de `dados`. Não
calcula SHA-256 de todos os downloads nessa checagem rápida.
Parquets e configurações antigos são preservados para a fase manual seguinte.
