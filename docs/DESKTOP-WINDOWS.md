# Assistente Windows (.NET MAUI)

Interface gráfica Windows x64, adicionada após a versão 0.1.0. O motor, API, TUI e CLIs continuam disponíveis para Windows/Linux; esta interface MAUI é exclusivamente Windows. Requer Windows 10 1809 ou superior em x64, preferencialmente uma versão do Windows ainda suportada.

## Executar

Na pasta distribuída, abra `GovData.Downloads.Desktop.exe`. Preserve todas as DLLs e subpastas ao extrair o pacote. A publicação inclui o runtime .NET e Windows App SDK; não depende do SDK de desenvolvimento no computador do usuário. O executável não tem assinatura comercial.

Para compilar na raiz do repositório:

```powershell
./eng/publish-desktop-windows.ps1
```

SDK, workloads, caches e publicação ficam em diretórios ignorados pelo Git. Os projetos novos são `GovData.Downloads.Desktop` (MAUI), `GovData.Downloads.Desktop.Flow` (integração ao motor) e `GovData.Downloads.Desktop.Flow.Tests` (verificações do fluxo).

## Primeiro uso: fonte pequena

1. Selecione **IBGE • Municípios do Censo 2022 (326 KB)**.
2. Clique em **Continuar**, escolha a pasta de destino e avance.
3. Confira fonte, endereço e destino. Clique em **Baixar e organizar**.
4. Aguarde a conclusão. A fonte validada gera 5.570 linhas em um Parquet, com manifesto READY e prévia de cinco linhas. Clique em **Abrir pasta dos resultados**.

O arquivo é baixado diretamente do IBGE, não de produção. Se o órgão retirar ou alterar o arquivo, a operação pode falhar e deve ser reavaliada. Não se inventam resultados em caso de indisponibilidade.

## Cadastro guiado

**Cadastrar uma fonte CSV** abre quatro etapas: identificação, endereço/arquivo, leitura das colunas e revisão. Cada etapa tem um botão de ajuda com explicação. Os identificadores, catálogo e contrato são gerados automaticamente. O cadastro é salvo localmente na pasta de dados do aplicativo e reaparece na próxima execução.

O cadastro mínimo aceita um CSV público por fonte, HTTPS direto sem credenciais/parâmetros, cabeçalho com letras sem acento/números/sublinhado, separador vírgula/ponto e vírgula/tabulação e UTF-8/Windows-1252. Informe os nomes exatos das colunas desejadas. O motor mantém os valores como texto; o cadastro simples não certifica unicidade ou limpeza de negócio. ZIP/Excel/DBC e layouts complexos exigem as ferramentas avançadas; a fonte IBGE pronta reutiliza seu contrato específico de ZIP.

Salvar não inicia o download. O usuário confirma o endereço na revisão do fluxo principal. Domínios com resolução para endereços locais/reservados continuam bloqueados pela política do motor. O aplicativo não consulta o PostgreSQL nem a API da empresa.

## Operação e retomada

Cada fonte recebe uma subpasta `GovData/ID` dentro do destino, com `originais`, `conjuntos`, `catalogo.json`, registros de download, `organizados/organization-run.json` e `resultado-desktop.json`. A execução usa uma transferência por vez e 512 MB configurados para DuckDB, preservando as verificações de armazenamento do motor. A janela permanece responsiva durante o processamento.

**Interromper com segurança** solicita cancelamento; aguarde a confirmação. O botão **Tentar novamente** reexecuta o fluxo com o mesmo destino e retoma trabalhos pausados quando suportado pelo motor. Uma organização incompleta não é apresentada como resultado novo concluído. Não há garantia de retomada de cada etapa interna. O assistente possui tempo limite de dez minutos, adequado ao recorte de arquivos pequenos; cargas massivas permanecem na CLI/TUI.

## Acessibilidade e limites

Controles padrão MAUI, nomes semânticos, cabeçalhos, textos explicativos, estados por escrito, navegação de teclado, botões de pelo menos 48 unidades, contraste de texto escuro em fundo claro e janela redimensionável com rolagem. As etapas podem ser revistas antes da confirmação. A automação reduz a digitação repetitiva e dispensa edição manual de JSON para o recorte suportado.

Essas medidas concretizam aspectos dos sete Princípios do Desenho Universal; não constituem certificação de acessibilidade. Validar com Narrador, ampliação de tela e pessoas sem familiaridade técnica antes de declarar atendimento integral. O cadastro de um CSV ainda exige reconhecer cabeçalho, separador e codificação.

## Verificar o fluxo

```powershell
./.dotnet/dotnet.exe run --project GovData.Downloads.Desktop.Flow.Tests -c Release
# Opcional: acrescenta somente o download público pequeno do IBGE
./.dotnet/dotnet.exe run --project GovData.Downloads.Desktop.Flow.Tests -c Release -- --live-ibge
```

O teste padrão usa transporte simulado, mas percorre catálogo, download, recibo, organizador, Parquet e manifesto reais. O teste IBGE usa a rede e confere fisicamente a contagem no Parquet. Esses testes da integração não substituem a observação de uso da interface por participantes.
