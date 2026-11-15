# Publicação do código

O código próprio está sob MIT, Copyright (c) 2026 Guilherme Ceolin Ganzer. Os avisos de terceiros devem acompanhar o código e os componentes distribuídos. Os binários oficiais json-everything conservam os termos OSMFEULA; não foram recompilados neste fechamento.

O primeiro commit registra o estado consolidado atual. Não representa um commit existente na implantação ou na entrevista. A versão histórica aplicada deve ser identificada pelo pacote preservado e pelas evidências da implantação, não por uma tag retroativa.

## Publicar no GitHub pessoal

Autentique o GitHub CLI e substitua `SEU_USUARIO` pela conta pessoal. Execute na raiz deste projeto:

```bash
gh auth login -h github.com
gh repo create SEU_USUARIO/govdata-manager --public --source=. --remote=origin --push
```

Se o repositório já existir, configure o remoto correspondente em vez de criar outro. Não force push. Confirme que a execução da CI no GitHub terminou antes de marcar uma release estável. A CI inclui compilação e testes sintéticos em Windows e Linux; sua execução remota só acontece depois da publicação.

O SDK `.dotnet`, caches `.local`, dados, credenciais, binários e o arquivo de orientação local `AGENTS.md` ficam fora do Git. Entrevistas, capturas do servidor e documentos acadêmicos permanecem fora da pasta do produto. A documentação e os exemplos de configuração do produto fazem parte do repositório.

## Identificação da entrega

Após publicar, registre no relatório o endereço real do repositório e, se necessário, o hash retornado por `git rev-parse HEAD`. Não apresente uma publicação ainda não efetuada como resultado obtido. Para uma futura distribuição de binários, complete o inventário dos componentes nativos e Python efetivamente incluídos.
