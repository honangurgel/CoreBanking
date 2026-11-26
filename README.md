# CoreBanking

Core bancário didático para portfólio, desenvolvido com .NET e utilizando exclusivamente dados e movimentações fictícios. O projeto simula operações financeiras e integrações externas para demonstrar conceitos de arquitetura, consistência e processamento assíncrono.

> **Aviso:** este projeto não é uma instituição financeira, não movimenta dinheiro real e não se conecta ao Banco Central do Brasil (BACEN), ao Pix ou a qualquer instituição financeira. A integração descrita como BACEN é um simulador local.

## Objetivos

- Implementar operações bancárias simuladas com regras explícitas e trilha de auditoria.
- Demonstrar um ledger de dupla entrada, idempotência e consistência transacional.
- Simular integrações externas resilientes e processamento assíncrono.
- Exercitar testes, observabilidade, documentação e execução local com ferramentas gratuitas e open source.

## Produtos e escopo

### Primeira fase

- Clientes fictícios e abertura de contas.
- Consulta de saldo e extrato.
- Depósitos e saques simulados.
- Transferências entre contas do sistema.
- Pix simulado, com chaves fictícias, limites e comprovantes.
- Agendamento e cancelamento de operações, quando permitido pelo estado.
- Comunicação simulada com serviços externos para consultas, comunicações, liquidação e reporte.

### Evoluções planejadas

- Cartões virtuais de débito e crédito, autorizações e faturas.
- Empréstimos fictícios, parcelas e pagamentos.
- Poupança e simulação de rendimentos.
- Seguros simulados.

Os produtos são referências de domínio comuns no varejo bancário brasileiro; não representam ofertas, regras ou serviços de uma instituição específica.

## Arquitetura técnica

A primeira versão será um **monólito modular**, com API e workers que podem ser executados como processos separados. Essa abordagem mantém o desenvolvimento e a execução local simples, enquanto permite escalar os workers independentemente. A separação em microserviços só será considerada se houver uma necessidade concreta.

```text
Aplicações cliente
        |
        v
ASP.NET Core API
  |          \
  |           \-- Outbox transacional
  |                    |
  v                    v
PostgreSQL        Broker de mensagens
                       |
                       v
                Workers de integração
                       |
                       v
                Simulador BACEN
                       |
                       v
                 Inbox / estados
```

### Stack inicial

| Componente | Tecnologia planejada |
| --- | --- |
| Linguagem e runtime | C# / .NET 10 LTS |
| API HTTP | ASP.NET Core |
| Persistência relacional | PostgreSQL 18 |
| Acesso a dados | Entity Framework Core e provedor Npgsql compatível |
| Ambiente local | Docker Compose |
| Mensageria | RabbitMQ, executado localmente em container |
| Automação | GitHub Actions |

As versões de dependências são registradas nos arquivos do projeto e devem ser atualizadas conforme compatibilidade e suporte. Clientes, contas, depósitos, transferências internas e Pix fictícios têm implementação inicial.

### Módulos de domínio

- **Clientes:** cadastro de personagens fictícios e dados de demonstração.
- **Contas:** abertura, estado, titularidade e consulta.
- **Ledger:** lançamentos contábeis e projeções de saldo.
- **Pagamentos:** transferências internas e Pix simulado.
- **Integração:** adaptador do simulador externo, limites de taxa e processamento assíncrono.
- **Cartões e crédito:** funcionalidades planejadas para fases posteriores.
- **Auditoria:** histórico de transições e resultados relevantes.

Os módulos terão limites de responsabilidade explícitos. A implementação poderá organizá-los em projetos .NET separados dentro de uma única solução, sem introduzir chamadas de rede entre módulos locais.

## Fluxo de integração assíncrona

1. A API valida a requisição e sua chave de idempotência.
2. Em uma transação do PostgreSQL, registra a operação e o evento correspondente na **Outbox**.
3. Retorna um identificador de operação e o estado inicial, sem aguardar o simulador externo.
4. Um worker publica ou consome o evento e chama o adaptador do simulador, respeitando limites configurados.
5. A resposta é registrada de forma idempotente por meio de uma **Inbox**, e a operação avança para seu estado final ou para tratamento de falha.
6. O cliente consulta o estado pelo identificador da operação.

Estados de referência:

```text
Received -> Queued -> Processing -> Completed
                                \-> Failed
                                \-> RequiresReview
```

Uma operação que dependa de confirmação externa não será apresentada como concluída antes dessa confirmação. Tentativas repetidas deverão ser seguras: a entrega de mensagens poderá ocorrer mais de uma vez, e consumidores idempotentes evitarão efeitos duplicados. Falhas temporárias terão novas tentativas com atraso progressivo; mensagens que excederem a política serão encaminhadas para uma fila de falhas para inspeção e reprocessamento controlado.

## Regras financeiras e consistência

- Valores monetários serão representados em unidades menores inteiras (por exemplo, centavos para BRL), sem `float` ou `double`.
- Cada lançamento contábil terá ao menos duas partidas e deverá fechar em zero por moeda.
- Lançamentos confirmados serão imutáveis. Correções serão representadas por lançamentos compensatórios, não pela edição do histórico.
- Saldo será derivado do ledger ou mantido como projeção atualizável na mesma transação que registra os lançamentos.
- Operações concorrentes deverão preservar os invariantes financeiros no banco de dados.
- Chaves de idempotência terão escopo e política de retenção definidos; requisições repetidas não poderão executar a mesma operação financeira duas vezes.
- A trilha de auditoria registrará identificadores, estados e horários sem incluir segredos ou dados pessoais reais.

### Ledger e movimentações fictícias

O componente interno de ledger utiliza contas contábeis vinculadas a contas de clientes ou destinadas a contrapartidas do sistema. Cada partida usa centavos em BRL: valores positivos aumentam o saldo e negativos o diminuem. Uma conta de cliente não pode ficar negativa; uma conta de contrapartida pode.

O lançamento exige ao menos duas contas distintas, valores não nulos e soma zero. A migration adiciona proteções no PostgreSQL para rejeitar lançamentos incompletos ou desbalanceados, alterações de histórico e atualizações diretas de saldo. A confirmação atualiza as projeções de saldo na mesma transação, com bloqueio das contas em ordem determinística. Um estorno contém as partidas opostas ao lançamento original e não pode ser aplicado duas vezes ao mesmo lançamento.

Uma conta contábil de saldo zero é criada na mesma transação da conta de cliente; uma migration cria contas contábeis para contas já existentes. A API consulta a projeção e permite depósitos e transferências internos fictícios. Depósitos utilizam uma conta interna de contrapartida. Transferências com saldo insuficiente são registradas como rejeitadas, sem lançamentos.

Depósitos e transferências exigem o cabeçalho `Idempotency-Key` (até 128 caracteres, sem espaços nas extremidades). A chave é única por tipo de operação e é mantida sem expiração. Repetir dados idênticos retorna o mesmo resultado; repetir a chave com dados diferentes retorna `409`. Uma transferência rejeitada por saldo insuficiente mantém sua rejeição: uma nova tentativa exige outra chave. Contas inexistentes e entradas inválidas não reservam a chave.

Uma transferência concluída grava o evento na Outbox junto com o lançamento e a operação. O worker publica eventos persistentes com confirmação no RabbitMQ; um consumidor registra a Inbox antes de confirmar a mensagem, deduplicando entregas. Falhas de processamento explicitamente sinalizadas recebem até três tentativas por sequência de entrega, com atrasos de 1 e 2 segundos em filas quorum; após a terceira falha, a mensagem vai para `corebanking.transfers.failed`. Mensagens inválidas também vão diretamente para essa fila. Em falhas de infraestrutura, o worker registra o erro e tenta reconectar com atraso progressivo, sem consumir o limite de falhas de processamento.

Como a entrega é pelo menos uma vez, uma interrupção entre a publicação e a confirmação pode gerar cópias e mais tentativas totais do que o limite por sequência. Erros inesperados interrompem o worker, em vez de serem tratados silenciosamente. O consumidor atual apenas registra a Inbox: futuros efeitos de negócio deverão ser gravados atomicamente com ela. Isso não representa comunicação ou liquidação com o BACEN.

## Escala e metas de carga

O cenário de referência para o **simulador de integração** é de até **10.000 requisições por minuto** (aproximadamente 167 por segundo em média). Isso é uma meta de projeto e de teste, não uma capacidade já medida ou garantida. O plano de carga deverá incluir picos acima da média, atrasos do simulador, falhas, mensagens duplicadas, concorrência por conta e recuperação após indisponibilidade.

O sistema deverá observar, no mínimo:

- taxa de entrada e saída;
- tamanho e idade da mensagem mais antiga na fila;
- latência por operação e por integração;
- erros, tentativas, mensagens em fila de falhas e reprocessamentos;
- divergências entre ledger e projeções de saldo.

Aumentar o número de workers não deve permitir exceder o limite configurado para o serviço externo simulado. Nenhum número de instâncias ou meta de latência será tratado como garantia até que haja testes de carga reproduzíveis.

## Segurança e dados fictícios

- Utilizar apenas dados sintéticos gerados para desenvolvimento e demonstração.
- Não inserir credenciais, tokens, chaves ou connection strings com segredos no repositório.
- Configurações locais sensíveis devem ser fornecidas por variáveis de ambiente ou user secrets.
- Não usar CPFs, chaves Pix ou dados de cartões de pessoas reais. Dados de exemplo devem ser claramente identificados como fictícios e não devem ser usados para validar identidades reais.
- O simulador externo não fará chamadas de rede ao BACEN nem a participantes do Pix.

## Requisitos para desenvolvimento local

Após a implementação da estrutura inicial, o ambiente de desenvolvimento deverá ter:

- Windows, macOS ou Linux;
- .NET 10 SDK;
- Docker Engine/Desktop com Docker Compose;
- Git.

Verifique as ferramentas instaladas:

```powershell
dotnet --version
docker --version
docker compose version
git --version
```

### Executar localmente

Com o Docker Engine ativo, execute na raiz do repositório. Os comandos abaixo usam PowerShell:

```powershell
docker compose up -d
dotnet tool restore
dotnet restore
dotnet build

# Credenciais exclusivamente locais do Compose; substitua se configurou outras.
$env:ConnectionStrings__Banking = "Host=localhost;Port=5432;Database=corebanking;Username=corebanking;Password=corebanking_local"
dotnet ef database update --project .\src\CoreBanking.Infrastructure --startup-project .\src\CoreBanking.Infrastructure
# Necessários para o health check de prontidão da API.
$env:RabbitMQ__ConnectionString = "amqp://corebanking:corebanking_local@localhost:5672/"
$env:Pix__SimulatorUrl = "http://localhost:5272"
dotnet run --project .\src\CoreBanking.Api
```

O Compose publica as portas apenas em `localhost`. Para uso local, o PostgreSQL e o RabbitMQ usam por padrão o usuário `corebanking` e a senha `corebanking_local`; são credenciais exclusivas de desenvolvimento e não devem ser reutilizadas em outros ambientes. O painel local do RabbitMQ fica em `http://localhost:15672`.

Execute `dotnet test` em outro terminal: os testes de integração exigem Docker ativo e criam um PostgreSQL isolado e temporário. As migrations não são aplicadas automaticamente ao iniciar a API; execute o comando acima após atualizá-las. Para utilizar a API em um novo terminal, defina novamente `ConnectionStrings__Banking` antes de executar `dotnet run`.

O GitHub Actions também executa restore, build e testes em pushes e pull requests, usando um runner Linux com Docker. Isso permite validar o código no CI sem alterar a política de segurança do Windows, mas não garante que os executáveis serão aceitos localmente.

Para executar o worker em outro terminal, após aplicar as migrations:

```powershell
$env:ConnectionStrings__Banking = "Host=localhost;Port=5432;Database=corebanking;Username=corebanking;Password=corebanking_local"
$env:RabbitMQ__ConnectionString = "amqp://corebanking:corebanking_local@localhost:5672/"
dotnet run --project .\src\CoreBanking.Worker
```

Para inspecionar falhas, use o painel local do RabbitMQ e consulte logs e conteúdo antes de agir. Após corrigir a causa, execute o comando abaixo em outro terminal com `RabbitMQ__ConnectionString` configurada. Ele reenvia **uma mensagem do início** da fila de falhas e reinicia seu contador; não seleciona um ID específico nem altera o conteúdo. Mensagens inválidas retornarão à fila até serem corrigidas por procedimento apropriado. Repita apenas após verificar o resultado.

```powershell
dotnet run --project .\src\CoreBanking.Worker -- --reprocess-failed
```

Uma interrupção durante o reenvio pode produzir duplicatas; a Inbox evita registros duplicados. Não altere manualmente o ledger para reprocessar eventos.

### Simulador de liquidação

O projeto `CoreBanking.Simulator` fornece um contrato **fictício e independente** para desenvolver a integração. Execute-o localmente:

```powershell
dotnet run --project .\src\CoreBanking.Simulator --launch-profile http
```

Em `http://localhost:5272`, `GET /api/v1/institutions` apresenta um pequeno catálogo estático de nomes e ISPBs públicos. Os dados são referências, não representam conexão com as instituições e não são uma lista atualizada automaticamente. Fonte: [participantes do SPI — Banco Central](https://www.bcb.gov.br/content/estabilidadefinanceira/spi-pdf/participantes-spi-20261120.pdf).

`POST /api/v1/settlements` recebe `operationId` e `keyId` como UUIDs fictícios, `institutionIspb`, `direction` (`Outgoing` ou `Incoming`), `amountMinor`, `scenario` (`Confirm`, `Reject` ou `Unavailable`) e `delayMilliseconds` (0 a 10000). Para a mesma requisição, devolve a mesma decisão fictícia; `Unavailable` retorna HTTP 503. O serviço não guarda solicitações, não registra liquidações, não valida propriedade de chaves e não movimenta saldos reais. O worker consome suas respostas somente no fluxo fictício.

### Pix fictício

O fluxo inicial implementa envio e recebimento **exclusivamente simulados**. O limite é R$ 999.999.999.999.999,99 por operação (99.999.999.999.999.999 centavos). Uma chave local fictícia é um UUID criado por `POST /api/v1/pix/keys`, com `{"accountId":"..."}`. Para recebimento é necessário informar uma chave criada para a conta; no envio, o UUID representa uma chave externa fictícia e não é consultado em nenhum diretório real.

`POST /api/v1/pix/operations` exige `Idempotency-Key` (mesmas regras das transferências) e recebe `accountId`, `keyId`, `institutionIspb`, `direction` (`Outgoing` ou `Incoming`), `amountMinor` e `scenario` (`Confirm` ou `Reject`). Retorna `202` para operação aceita e o estado atual também nos replays; requisições com a mesma chave e dados diferentes retornam `409`. Saldo insuficiente é persistido e exige nova chave para tentar novamente. `GET /api/v1/pix/operations/{id}` consulta o estado; `GET /api/v1/pix/operations/{id}/receipt` traz os lançamentos e a trilha de estados, inclusive para operações não confirmadas.

No envio, o lançamento de reserva reduz o saldo disponível da conta imediatamente e credita uma conta contábil interna; confirmação transfere a reserva para compensação fictícia, enquanto rejeição devolve o valor à conta. No recebimento, o crédito só ocorre após confirmação. Cada mudança de estado e lançamento é persistida atomicamente. O próprio registro `Pending` funciona como fila durável no PostgreSQL, independente da Outbox de transferências internas; workers usam bloqueios `SKIP LOCKED` para evitar processamento simultâneo. A chamada ao simulador é determinística para a mesma operação e pode ser repetida após interrupção.

Configure o endereço **local** antes de iniciar o worker:

```powershell
$env:Pix__SimulatorUrl = "http://localhost:5272"
```

O worker aceita somente origem HTTP loopback e não segue redirecionamentos. Sem configuração, o processamento Pix fica desativado e as operações permanecem pendentes. Falhas temporárias do simulador são tentadas três vezes, com atrasos de 1 e 2 segundos. Após três falhas, ou resposta inválida, a operação passa a `RequiresReview` e a reserva permanece intacta. Depois de investigar e corrigir a causa, `POST /api/v1/pix/operations/{id}/retry` reinicia as tentativas mantendo os dados e o cenário originais; nunca libere a reserva manualmente por suposição. Uma indisponibilidade do PostgreSQL é tentada novamente pelo worker. As filas e as políticas Pix não usam a fila RabbitMQ de falhas de transferências.

As chamadas Pix ao simulador são limitadas globalmente por banco: 600 permissões por janela móvel de 60 segundos e 10 chamadas simultâneas por padrão. O limite é compartilhado por workers conectados ao mesmo PostgreSQL; operações que aguardam capacidade permanecem pendentes sem consumir tentativas. São permitidos picos até o limite, não há distribuição uniforme da taxa. A permissão é registrada antes da chamada e permanece contabilizada após falhas ou interrupções, mesmo se a chamada não ocorrer.

Os limites são valores de desenvolvimento, não uma capacidade medida. Para alterá-los, pare todos os workers, atualize a única linha de `"PixThrottleSettings"` no PostgreSQL (`"RequestsPerMinute"` de 1 a 100000; `"MaxConcurrent"` de 1 a 100) e reinicie todos com esta versão. Não execute versões antigas dos workers simultaneamente, pois elas não respeitam o limite. Os bloqueios de concorrência dependem da conexão PostgreSQL; interrupções abruptas e atrasos entre a concessão e a chamada não constituem uma garantia rígida de taxa ou concorrência no simulador. O acesso direto ao simulador não passa por este controle.

O simulador não persiste suas respostas nem representa BACEN, DICT ou liquidação real. Nesta demonstração a decisão é escolhida na requisição, sem autenticação, apenas para testes locais; não use o sistema para movimentação real.

`GET /health` verifica apenas se a API está em execução. `GET /health/ready` verifica conexão e consulta ao PostgreSQL, conexão autenticada ao RabbitMQ e resposta de saúde do simulador local. Configure as três variáveis no processo da API e inicie o simulador; a ausência ou indisponibilidade de qualquer dependência resulta em HTTP 503 e estado `Unhealthy`, incluindo quando o Pix está desativado no worker. O resultado contém apenas o estado geral e os estados `postgres`, `rabbitmq` e `simulator`, sem detalhes de conexão. A verificação não confirma migrations, filas, funcionamento dos workers ou capacidade de processamento. Cada consulta abre uma conexão ao broker: use frequência moderada e não exponha a API publicamente.

A API limita requisições por IP em janela fixa de um minuto, por instância, com padrão de 600 e resposta HTTP 429 sem fila. O valor pode ser alterado por `RateLimiting__PermitsPerMinute`; as rotas sob `/health` são isentas. Sem configuração confiável de proxy, o endereço observado pode ser compartilhado por vários clientes; não é um limite distribuído nem uma proteção de segurança.

### Logs e correlação de operações

API, worker e simulador emitem logs de console em JSON estruturado. Para acompanhar um Pix, busque `OperationId` (o `id` retornado pela API) nos logs da API, do worker e do simulador. Para transferências, busque `TransferId`; o worker também registra `MessageId` na publicação e no consumo, inclusive em falhas e reentregas. A API registra `RequestId` junto ao identificador retornado para vincular cada requisição à operação; replays idempotentes mantêm o mesmo identificador de operação. Reprocessamentos Pix registram se foram aceitos. Logs após processamento indicam o estado persistido, não garantem que uma linha foi emitida antes de uma interrupção.

Esta é uma correlação por identificador de negócio, não tracing distribuído de spans. Não são registrados corpos, chaves de idempotência ou valores nos eventos adicionados; configure e proteja também os logs do framework e da infraestrutura. O projeto não armazena logs em banco nem em buffers próprios: configure coleta e retenção externamente. As mensagens registram apenas campos pequenos e não serializam payloads inteiros, mas o impacto no LOH e na vazão ainda precisa ser medido em testes de carga.

### Primeiro fluxo de uso

Com a API em execução no perfil HTTP padrão (`http://localhost:5027`), crie um cliente e use o identificador retornado para abrir uma conta:

```powershell
$customer = Invoke-RestMethod -Method Post -Uri http://localhost:5027/api/v1/customers -ContentType "application/json" -Body '{"name":"Cliente Fictício"}'
$body = @{ customerId = $customer.id } | ConvertTo-Json
$account = Invoke-RestMethod -Method Post -Uri http://localhost:5027/api/v1/accounts -ContentType "application/json" -Body $body
Invoke-RestMethod -Uri "http://localhost:5027/api/v1/accounts/$($account.id)"
```

O nome deve ter de 1 a 200 caracteres após remover espaços nas extremidades. Cada conta é criada em estado `Active` para um cliente existente, com saldo inicial zero. É possível criar múltiplas contas por cliente; ainda não há fechamento ou bloqueio. Não existe autenticação: a API é para demonstração local, não para exposição pública.

Para criar dados sintéticos e creditá-los via ledger, com a API em execução:

```powershell
.\scripts\seed-demo.ps1 -Count 3
```

O script cria novos clientes e contas a cada execução, usa nomes aleatórios explicitamente fictícios e deposita 100.000 centavos por conta. Não apaga dados; se houver falha, os recursos já criados permanecem e seus IDs são exibidos. Não gera CPF, chaves Pix ou dados de cartões.

## API

| Método | Endpoint | Finalidade |
| --- | --- | --- |
| `GET` | `/health` | Verificar se a API está em execução |
| `GET` | `/health/ready` | Verificar conectividade das dependências |
| `POST` | `/api/v1/customers` | Cadastrar cliente fictício |
| `GET` | `/api/v1/customers/{customerId}` | Consultar cliente |
| `POST` | `/api/v1/accounts` | Abrir conta |
| `GET` | `/api/v1/accounts/{accountId}` | Consultar conta e saldo em centavos (`balanceMinor`, moeda `BRL`) |
| `POST` | `/api/v1/deposits` | Depósito fictício; corpo `accountId`, `amountMinor` |
| `GET` | `/api/v1/deposits/{depositId}` | Consultar depósito |
| `POST` | `/api/v1/transfers` | Transferência interna; corpo `sourceAccountId`, `destinationAccountId`, `amountMinor` |
| `GET` | `/api/v1/transfers/{transferId}` | Consultar transferência |

As criações retornam `201 Created` e o endereço do recurso em `Location`; entradas inválidas retornam `400`, e identificadores inexistentes retornam `404`. A criação de cliente e conta ainda não implementa idempotência. Extratos, Pix e consulta de operações externas assíncronas estão planejados para fases posteriores.

## Testes

Os testes existentes cobrem a validação de domínio, o endpoint de saúde e o fluxo HTTP de cliente e conta com PostgreSQL isolado. Conforme os módulos forem implementados, serão adicionados:

- Testes unitários das regras de domínio.
- Testes de integração usando PostgreSQL real em container.
- Verificações de lançamentos balanceados e imutabilidade do ledger.
- Testes de idempotência, concorrência, duplicação e ordenação de mensagens.
- Testes de falha e recuperação da integração simulada.
- Testes de carga que comparem taxa de processamento, atraso de fila e consistência financeira.

## Organização inicial de diretórios

```text
src/
  CoreBanking.Api/
  CoreBanking.Domain/
  CoreBanking.Application/
  CoreBanking.Infrastructure/
  CoreBanking.Worker/
tests/
  CoreBanking.UnitTests/
  CoreBanking.IntegrationTests/
```

A organização poderá evoluir durante a implementação, preservando os limites modulares e a separação entre regras de domínio, casos de uso e infraestrutura.

## Status

**Implementação inicial.** Clientes, contas, ledger, depósitos, transferências e Pix fictícios estão em desenvolvimento. O simulador não é uma integração real ou homologada. As entregas seguintes e seus critérios de aceite estão descritos no [Fluxo de desenvolvimento](./DEVELOPMENT_FLOW.md).

## Referências

- [Política de suporte do .NET](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)
- [Versões suportadas do PostgreSQL](https://www.postgresql.org/support/versioning/)
- [Provedores do Entity Framework Core](https://learn.microsoft.com/en-us/ef/core/providers/)
- [Pix — Banco Central do Brasil](https://www.bcb.gov.br/estabilidadefinanceira/pix)
- [Open Finance — Banco Central do Brasil](https://www.bcb.gov.br/estabilidadefinanceira/openfinance)
