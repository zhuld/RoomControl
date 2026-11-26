<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'

// 登录页背景动画：粒子缓慢漂浮，靠近时互相连线，指针附近轻微排斥。
interface Particle {
  x: number
  y: number
  vx: number
  vy: number
}

const props = withDefaults(
  defineProps<{
    color?: string
    count?: number
    maxDistance?: number
    speed?: number
  }>(),
  { color: '', count: 0, maxDistance: 150, speed: 0.3 },
)

const REPEL_RADIUS = 150

const canvasEl = ref<HTMLCanvasElement | null>(null)
let context: CanvasRenderingContext2D | null = null
let particles: Particle[] = []
let width = 0
let height = 0
let frameHandle = 0
let resizeObserver: ResizeObserver | undefined
let strokeColor = '#2f6f55'
const pointer = { x: Number.NaN, y: Number.NaN }

// 未指定颜色时跟随主题强调色；主题切换后由下一次尺寸变化重新读取。
function resolveColor() {
  if (props.color) return props.color
  return getComputedStyle(document.documentElement).getPropertyValue('--accent').trim() || '#2f6f55'
}

function particleCount() {
  if (props.count > 0) return props.count
  // 按面积决定数量，窄屏不会过于拥挤。
  return Math.min(110, Math.max(30, Math.round((width * height) / 14_000)))
}

function newParticle(): Particle {
  return {
    x: Math.random() * width,
    y: Math.random() * height,
    vx: (Math.random() - 0.5) * 2 * props.speed,
    vy: (Math.random() - 0.5) * 2 * props.speed,
  }
}

// 容器尺寸变化时只增减粒子，避免拖动窗口时整片粒子重新排布。
function syncParticles() {
  const target = particleCount()
  while (particles.length > target) particles.pop()
  while (particles.length < target) particles.push(newParticle())
  for (const particle of particles) {
    if (particle.x > width) particle.x = Math.random() * width
    if (particle.y > height) particle.y = Math.random() * height
  }
}

function resize() {
  const canvas = canvasEl.value
  if (!canvas) return
  const rect = canvas.getBoundingClientRect()
  width = Math.max(1, Math.round(rect.width))
  height = Math.max(1, Math.round(rect.height))
  // 高分屏按设备像素比放大画布，超过 2 倍只会增加开销。
  const ratio = Math.min(window.devicePixelRatio || 1, 2)
  canvas.width = Math.round(width * ratio)
  canvas.height = Math.round(height * ratio)
  context = canvas.getContext('2d')
  context?.setTransform(ratio, 0, 0, ratio, 0, 0)
  strokeColor = resolveColor()
  syncParticles()
}

function moveParticles() {
  for (const particle of particles) {
    particle.x += particle.vx
    particle.y += particle.vy
    if (particle.x <= 0 || particle.x >= width) {
      particle.vx = -particle.vx
      particle.x = Math.min(Math.max(particle.x, 0), width)
    }
    if (particle.y <= 0 || particle.y >= height) {
      particle.vy = -particle.vy
      particle.y = Math.min(Math.max(particle.y, 0), height)
    }
  }

  if (!Number.isFinite(pointer.x) || !Number.isFinite(pointer.y)) return
  for (const particle of particles) {
    const dx = particle.x - pointer.x
    const dy = particle.y - pointer.y
    const distanceSq = dx * dx + dy * dy
    if (distanceSq === 0 || distanceSq > REPEL_RADIUS * REPEL_RADIUS) continue
    const distance = Math.sqrt(distanceSq)
    const push = ((REPEL_RADIUS * REPEL_RADIUS - distanceSq) / (REPEL_RADIUS * REPEL_RADIUS)) * 1.4
    particle.x += (dx / distance) * push
    particle.y += (dy / distance) * push
  }
}

function render() {
  const ctx = context
  if (!ctx) return
  ctx.clearRect(0, 0, width, height)

  const color = strokeColor
  const linkDistanceSq = props.maxDistance * props.maxDistance
  const hasPointer = Number.isFinite(pointer.x) && Number.isFinite(pointer.y)
  ctx.strokeStyle = color
  ctx.fillStyle = color
  ctx.lineWidth = 1

  const drawLink = (x1: number, y1: number, x2: number, y2: number) => {
    const dx = x1 - x2
    const dy = y1 - y2
    const distanceSq = dx * dx + dy * dy
    if (distanceSq > linkDistanceSq) return
    ctx.globalAlpha = (1 - distanceSq / linkDistanceSq) * 0.35
    ctx.beginPath()
    ctx.moveTo(x1, y1)
    ctx.lineTo(x2, y2)
    ctx.stroke()
  }

  for (let i = 0; i < particles.length; i += 1) {
    const particle = particles[i]
    for (let j = i + 1; j < particles.length; j += 1) {
      const other = particles[j]
      drawLink(particle.x, particle.y, other.x, other.y)
    }
    if (hasPointer) drawLink(particle.x, particle.y, pointer.x, pointer.y)
  }

  ctx.globalAlpha = 0.5
  for (const particle of particles) {
    ctx.beginPath()
    ctx.arc(particle.x, particle.y, 1.6, 0, Math.PI * 2)
    ctx.fill()
  }
  ctx.globalAlpha = 1
}

function tick() {
  moveParticles()
  render()
  frameHandle = requestAnimationFrame(tick)
}

function handlePointerMove(event: PointerEvent) {
  const canvas = canvasEl.value
  if (!canvas) return
  const rect = canvas.getBoundingClientRect()
  pointer.x = event.clientX - rect.left
  pointer.y = event.clientY - rect.top
}

function handlePointerOut(event: PointerEvent) {
  // 指针离开窗口时才取消吸附，窗口内移动保持跟随。
  if (event.relatedTarget === null) {
    pointer.x = Number.NaN
    pointer.y = Number.NaN
  }
}

onMounted(() => {
  const canvas = canvasEl.value
  if (!canvas) return
  resize()
  resizeObserver = new ResizeObserver(resize)
  resizeObserver.observe(canvas)
  window.addEventListener('pointermove', handlePointerMove, { passive: true })
  window.addEventListener('pointerout', handlePointerOut, { passive: true })
  frameHandle = requestAnimationFrame(tick)
})

onBeforeUnmount(() => {
  cancelAnimationFrame(frameHandle)
  resizeObserver?.disconnect()
  window.removeEventListener('pointermove', handlePointerMove)
  window.removeEventListener('pointerout', handlePointerOut)
})
</script>

<template>
  <div class="canvas-nest" aria-hidden="true">
    <canvas ref="canvasEl"></canvas>
  </div>
</template>
