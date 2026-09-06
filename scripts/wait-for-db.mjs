import { execSync } from 'node:child_process'

const deadline = Date.now() + 60_000
while (Date.now() < deadline) {
  try {
    execSync('docker compose exec -T postgres pg_isready -U lis', { stdio: 'ignore' })
    console.log('postgres ready')
    process.exit(0)
  } catch {
    await new Promise((r) => setTimeout(r, 1000))
  }
}
console.error('postgres did not become ready in 60s')
process.exit(1)
