#!/usr/bin/env bash
# Carga no Bronze + verificação, por função, retomável.
#
# Uso (como usuário autorizado do serviço, em qualquer pasta):
#   export LOADER=/opt/govdata-manager/arquivo-loader/GovData.Downloads.ArquivoLoader
#   export CONFIG=<config do Loader>
#   bash /mnt/dados/govdata-manager/eng/arquivo/carregar-e-verificar.sh funcao [funcao...]
#
# Para cada função: pasta de organização READY mais recente -> register-run ->
# load em rodadas de 100 partições até esgotar -> verify-data por publicação.
# Cada publicação aprovada vai para dados/logs/verificacao-ok.txt ("pasta|publicação")
# e é pulada nas próximas execuções; repetir o comando só faz o que falta.
# Carga e verificação só leem os Parquets; o Bronze só muda pelo Loader.
set -u -o pipefail
: "${LOADER:?defina LOADER}" "${CONFIG:?defina CONFIG}"
[ $# -gt 0 ] || { echo "Informe ao menos uma função (ex.: datasus-sinasc)."; exit 2; }
cd /mnt/dados/govdata-manager
mkdir -p dados/logs
OK=dados/logs/verificacao-ok.txt
touch "$OK"
PARTS=100
verificadas=0
puladas=0
falhas=()

for fn in "$@"; do
  RUN=$(for d in $(ls -1d "$PWD"/dados/data-to-transfer/*/"$fn"-2* 2>/dev/null | sort); do
          grep -q '"status": *"READY"' "$d/organization-run.json" 2>/dev/null && echo "$d"
        done | tail -1)
  if [ -z "$RUN" ]; then echo "SEM PASTA READY: $fn"; falhas+=("$fn:sem-pasta"); continue; fi
  echo "== $fn -> $RUN"

  if ! sudo -u nifi "$LOADER" register-run --config "$CONFIG" --run-dir "$RUN" </dev/null | tail -1; then
    falhas+=("$fn:register-run"); continue
  fi

  carga=ok
  while :; do
    out=$(sudo -u nifi "$LOADER" load --config "$CONFIG" --run-dir "$RUN" --max-partitions "$PARTS" </dev/null | tee /dev/stderr) \
      || { carga=erro; break; }
    n=$(sed -n 's/.*concluídas nesta chamada: \([0-9]*\).*/\1/p' <<<"$out" | tail -1)
    [ "${n:-0}" -lt "$PARTS" ] && break
  done
  if [ "$carga" = erro ]; then falhas+=("$fn:load"); continue; fi

  for id in $(python3 -I -c 'import json,sys; print(*json.load(open(sys.argv[1]))["publicationIds"])' "$RUN/organization-run.json"); do
    if grep -qxF "$RUN|$id" "$OK"; then puladas=$((puladas + 1)); continue; fi
    if sudo -u nifi "$LOADER" verify-data --config "$CONFIG" --run-dir "$RUN" --publication "$id" </dev/null; then
      echo "$RUN|$id" >> "$OK"
      verificadas=$((verificadas + 1))
    else
      falhas+=("$fn:$id")
    fi
  done
done

echo
echo "VERIFICADAS AGORA: $verificadas | JÁ VERIFICADAS ANTES (puladas): $puladas | FALHAS: ${#falhas[@]}"
if [ ${#falhas[@]} -gt 0 ]; then printf '  %s\n' "${falhas[@]}"; exit 1; fi
