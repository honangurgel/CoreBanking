# Fluxo de desenvolvimento

Este documento define a sequência de implementação do CoreBanking. O objetivo é entregar incrementos executáveis e verificáveis, mantendo os invariantes financeiros e a integração fictícia como prioridades.

## Premissas de trabalho

- Projeto pessoal de portfólio; clientes, contas, chaves e transações serão dados sintéticos.
- Nenhuma conexão será feita com o BACEN, o Pix ou instituições financeiras reais.
- O cenário de carga de referência é até 10.000 requisições por minuto para o simulador de integração. É uma meta a medir, não uma capacidade presumida.
- A aplicação começa como monólito modular .NET 10. API, worker e simulador poderão ser processos distintos, sem serem microserviços de negócio independentes.
- PostgreSQL será o sistema de registro financeiro; RabbitMQ será o broker local inicial. Ambos serão executados por Docker Compose.
- Cada etapa deve deixar a aplicação em estado compilável e os testes relevantes passando antes de avançarmos.

## Fases de implementação

### 0. Fechar decisões e critérios do primeiro incremento

**Objetivo:** converter a arquitetura em decisões técnicas verificáveis antes de criar código de domínio.

- Definir o formato da solução e dependências entre projetos.
- Fixar versões compatíveis do .NET, EF Core, Npgsql, PostgreSQL e RabbitMQ.
- Definir os estados e contratos iniciais para contas, lançamentos, transferências e operações assíncronas.
- Especificar o que significa sucesso e falha de uma transferência interna e de uma operação encaminhada ao simulador.

**Concluída quando:** as decisões estiverem registradas neste fluxo ou no README, sem perguntas de domínio bloqueando o esqueleto.

### 1. Criar a solução e o ambiente local

**Objetivo:** obter uma base que qualquer pessoa consiga compilar e testar.

- Criar a solução .NET e os projetos de API, domínio, aplicação, infraestrutura e worker.
- Criar projetos de testes unitários e de integração.
- Configurar `.editorconfig`, nullable reference types, análise estática e build warnings relevantes.
- Criar Docker Compose para PostgreSQL e RabbitMQ, com health checks e volumes de desenvolvimento.
- Adicionar workflow do GitHub Actions para restore, build e testes.

**Concluída quando:** `dotnet build` e `dotnet test` passam localmente e no CI; os containers iniciam e ficam saudáveis.

### 2. Implementar clientes e contas fictícias

**Objetivo:** estabelecer o primeiro fluxo de negócio persistido.

**Status:** implementação inicial criada, com cliente (nome e identificador) e conta ativa vinculada a um cliente existente. Fechamento, bloqueio e movimentação ainda não fazem parte deste incremento. A validação completa inclui executar os testes de integração com PostgreSQL em container.

- Definir entidades e regras de cliente e conta, incluindo seus estados e transições válidas.
- Criar migrations e persistência PostgreSQL.
- Implementar abertura e consulta de conta pela API.
- Gerar ou fornecer apenas dados sintéticos para demonstração; não usar dados pessoais reais.

**Concluída quando:** uma conta pode ser criada e consultada após reiniciar a aplicação; regras inválidas são rejeitadas e cobertas por testes.

### 3. Construir o ledger

**Objetivo:** tornar o registro contábil a fonte de verdade de toda movimentação.

**Status:** contas contábeis, partidas em centavos, projeções e estornos foram introduzidos. A abertura de conta cria sua conta contábil, a API consulta o saldo e as migrations do ledger foram aplicadas ao banco de desenvolvimento.

- Modelar lançamentos e partidas de dupla entrada por moeda.
- Representar BRL em centavos inteiros e proibir valores de ponto flutuante no domínio monetário.
- Tornar lançamentos confirmados imutáveis; representar correções por lançamentos compensatórios.
- Persistir lançamentos e projeção de saldo atomicamente.
- Definir proteção contra lançamentos desbalanceados no domínio e no limite de persistência.

**Concluída quando:** testes demonstram partidas balanceadas, imutabilidade, atomicidade e consistência entre saldo projetado e ledger, inclusive sob concorrência.

### 4. Implementar transferências internas e idempotência

**Objetivo:** movimentar saldo entre contas sem duplicidade nem saldo inconsistente.

**Status:** implementação em andamento, com depósitos fictícios para gerar saldo de demonstração e transferências internas idempotentes. As novas migrations ainda devem ser aplicadas após validação.

- Criar o caso de uso de transferência com débito e crédito na mesma transação PostgreSQL.
- Definir chave de idempotência, escopo, retenção e comportamento de repetição com mesma chave e conteúdo diferente.
- Definir estratégia de concorrência e ordem determinística de bloqueio das contas envolvidas.
- Expor criação e consulta de transferência pela API, com validação e erros explícitos.

**Concluída quando:** retries não duplicam lançamentos; duas solicitações concorrentes não permitem saldo inválido; testes cobrem falha antes e depois do commit.

### 5. Adicionar Outbox, broker e workers

**Objetivo:** aceitar operações assíncronas com entrega confiável entre banco e mensageria.

**Status:** Outbox, Inbox e worker de transferência incluem retries limitados por sequência de entrega e reprocessamento manual da primeira mensagem da fila de falhas. O processamento de operações externas e a validação de falhas e recuperação permanecem pendentes; duplicatas podem gerar mais tentativas totais que o limite por sequência.

- Gravar evento de Outbox na mesma transação do estado de negócio que o origina.
- Publicar eventos pendentes no RabbitMQ por meio de um dispatcher recuperável.
- Implementar Inbox ou mecanismo equivalente de deduplicação no consumidor.
- Definir estados de operação, correlação, retries com atraso progressivo e fila de falhas (dead-letter queue).
- Garantir que o processamento seja idempotente mesmo com entrega repetida.

**Concluída quando:** testes de falha entre commit e publicação, redelivery e indisponibilidade do broker não perdem operações nem aplicam efeitos duplicados.

### 6. Criar o simulador de integração

**Objetivo:** emular o serviço externo sem acessar sistemas reais.

**Status:** serviço independente com catálogo estático e respostas fictícias; worker Pix integrado por HTTP local, com estados persistidos e limites compartilhados no PostgreSQL. Os limites são configurações de desenvolvimento e não representam capacidade medida.

- Criar um serviço local com contratos fictícios para consulta, comunicação, liquidação e reporte.
- Usar nomes e códigos públicos de instituições reais apenas como referência; clientes, contas, chaves e liquidações serão fictícios, sem comunicação com instituições reais.
- Permitir configurar latência, falhas temporárias, rejeições de negócio, respostas duplicadas e indisponibilidade.
- Implementar adaptador de integração no worker, isolando protocolo externo do domínio.
- Aplicar limites de taxa e concorrência configuráveis e compartilhados entre instâncias de worker.
- Expor o estado assíncrono da operação à API para consulta pelo cliente.

**Concluída quando:** cenários de sucesso, rejeição, timeout, retry, duplicidade e recuperação são reproduzíveis em testes automatizados.

### 7. Implementar Pix simulado e limites operacionais

**Objetivo:** ampliar pagamentos usando o fluxo assíncrono e os invariantes já estabelecidos.

**Status:** envio e recebimento fictícios, limite de R$ 999.999.999.999.999,99 (99.999.999.999.999.999 centavos), reserva contábil, revisão manual e controle compartilhado das chamadas do worker foram introduzidos. O processamento é persistido no PostgreSQL e o simulador é determinístico e sem estado. A API tem rate limiting por IP e por instância; testes de carga e validação operacional ampliada permanecem pendentes.

- Criar chaves fictícias e regras de transferência Pix exclusivamente internas ao simulador.
- Implementar validação, limites por operação e estado pendente até confirmação necessária.
- Registrar comprovante e trilha de transições, sem expor a operação como liquidada antes da confirmação simulada.
- Aplicar rate limiting na entrada e controle de fluxo nos workers, preservando a capacidade de observar e drenar filas.

**Concluída quando:** limites e estados são cobertos por testes e a operação nunca é duplicada por retry ou mensagem repetida.

### 8. Observabilidade, resiliência e segurança

**Objetivo:** tornar falhas e gargalos detectáveis e o comportamento seguro para uma demonstração.

**Status:** a API expõe verificações de vida e prontidão para PostgreSQL, RabbitMQ e simulador. Elas não verificam o funcionamento dos workers ou a integridade dos dados. API, worker e simulador produzem logs estruturados correlacionáveis por identificadores de Pix e transferências. Há métricas do worker, backlog persistido e mensagens prontas no RabbitMQ; tracing distribuído, idade das mensagens RabbitMQ e métricas contínuas de divergência contábil permanecem pendentes. A API suporta OIDC com perfis operacionais independentes e modo local de desenvolvimento sem autenticação. Eventos operacionais de movimentação e reprocessamento são auditados transacionalmente, com escopo e limites descritos no README. O isolamento entre clientes continua ausente; não é adequada à exposição pública.

- Adicionar logs estruturados e correlação de ponta a ponta por operação.
- Instrumentar métricas de taxa, latência, tamanho/idade das filas, retries, falhas e divergência entre ledger e projeção.
- Configurar health checks de aplicação, PostgreSQL, broker e simulador.
- Revisar configuração de segredos, validação de entrada, retenção de dados sintéticos e tratamento de erros.
- Documentar procedimentos de reprocessamento e inspeção da fila de falhas.

**Concluída quando:** falhas simuladas são observáveis, nenhuma falha é convertida silenciosamente em sucesso e nenhuma credencial é necessária no repositório.

### 9. Teste de carga e demonstração end-to-end

**Objetivo:** avaliar a meta de 10.000 requisições por minuto e registrar os limites medidos.

**Status:** há cenário isolado de transferências concorrentes e outro com tráfego durante a recuperação do RabbitMQ. A carga Pix usa processos separados, interrupção de simulador ou worker, percentis aproximados de aceitação e amostragem opcional de LOH após GC. Os resultados e suas limitações estão no README. Permanecem pendentes rede representativa, latência de liquidação e validação da meta de capacidade.

- Criar cenários de carga reproduzíveis para taxa média e picos.
- Separar carga de entrada na API de capacidade do simulador e vazão dos workers.
- Testar contenção em contas concorridas, atrasos, falhas externas e recuperação de filas.
- Verificar invariantes financeiros após cada execução: partidas balanceadas, ausência de duplicatas e reconciliação de saldos.
- Registrar ambiente, configuração, duração, resultados e limitações; não afirmar capacidade além dos testes executados.
- Documentar um fluxo de demonstração local do cliente até a confirmação do simulador.

**Concluída quando:** os resultados permitem comparar a meta com medições reais e demonstram consistência após carga e falhas.

### 10. Expandir produtos

**Objetivo:** adicionar produtos sem comprometer o ledger e os limites dos módulos existentes.

Ordem sugerida:

1. Extrato paginado e consultas operacionais. **Status:** extrato contábil por conta com cursor e limite de página implementado; outras consultas operacionais permanecem pendentes.
2. Cartão de débito/crédito virtual, autorização e fatura simuladas.
3. Empréstimos fictícios, cronograma de parcelas e pagamentos.
4. Poupança, investimentos e seguros apenas como simulações didáticas.

Cada produto novo deve declarar seus estados, lançamentos no ledger, comportamento de idempotência e testes antes de ser exposto pela API.

## Critérios transversais para cada mudança

- Compilar e executar os testes afetados antes de integrar a etapa.
- Adicionar testes para regras novas e casos de falha; não depender apenas de testes de happy path.
- Manter API versionada e contratos documentados.
- Usar migrations revisáveis e manter compatibilidade entre mudanças de esquema e aplicação durante desenvolvimento.
- Evitar dados reais, segredos, integrações externas reais e afirmações não medidas de desempenho.
- Atualizar README e instruções de uso quando comandos, endpoints ou requisitos mudarem.

## Fluxo diário de desenvolvimento

1. Escolher uma entrega pequena da fase ativa e declarar seu critério de aceite.
2. Implementar regra de domínio e testes antes ou junto da integração externa/persistência.
3. Rodar testes unitários, integração relevante, build e análise estática.
4. Verificar invariantes financeiros e o comportamento em falha/retry, quando aplicável.
5. Atualizar documentação e exemplos de uso.
6. Só então iniciar a próxima entrega.

## Fora do escopo

- Integração real com BACEN, Pix, SPI, Open Finance ou qualquer instituição financeira.
- Uso de dinheiro, clientes, chaves Pix ou credenciais reais.
- Alegação de que o projeto está autorizado, certificado ou apto para produção bancária.
- Separação em microserviços antes de necessidade técnica demonstrada por medições.
