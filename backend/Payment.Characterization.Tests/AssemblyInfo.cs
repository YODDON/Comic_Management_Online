// The saga tests share RabbitMQ queues with fixed names ("debit-wallet", "unlock-chapter", ...), exactly like
// the real services, so test classes must not run at the same time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
