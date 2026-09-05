import { createInterface } from 'node:readline/promises'
import { AstmSimulator, type Misbehaviour } from './simulator.js'

const port = Number(process.env.SIM_PORT ?? 9100)
const misbehaviour = (process.env.SIM_MISBEHAVIOUR ?? 'none') as Misbehaviour

const sim = new AstmSimulator({ misbehaviour })
const actual = await sim.start(port)
console.log(`Máy giả lập ASTM đang lắng nghe ở 127.0.0.1:${actual} (chế độ: ${misbehaviour})`)
console.log('Nhấn Enter để gửi một phiên kết quả mẫu. Ctrl-C để thoát.')

const rl = createInterface({ input: process.stdin, output: process.stdout })
let counter = 1
for (;;) {
  await rl.question('')
  const sid = `SID${String(counter).padStart(4, '0')}`
  counter += 1
  try {
    await sim.sendResults([
      { sid, testCode: 'GLU', value: '5.4', unit: 'mmol/L' },
      { sid, testCode: 'URE', value: '6.1', unit: 'mmol/L' },
    ])
    console.log(`đã gửi kết quả cho ${sid}`)
  } catch (error) {
    console.error(`gửi thất bại: ${String(error)}`)
  }
}
