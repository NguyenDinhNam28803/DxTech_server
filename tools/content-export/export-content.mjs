// Xuất nội dung đang gắn cứng trong repo client (DXTechs) thành JSON cho lệnh `--seed-content` (ADR-015 §8, CT-1).
// Chạy (Node ≥ 22.6, không cần cài gói):
//   node --experimental-strip-types --no-warnings tools/content-export/export-content.mjs <thư-mục-repo-client> \
//        > server/Modules/Content/Seeding/content-seed.json
// Slug giữ ĐÚNG như web đang dùng: tin tức lấy `slug` có sẵn; giải pháp/đào tạo sinh bằng chính hàm `solutionSlug` của client.
// Dữ liệu trong ResourceView.vue (sách, tài liệu, thuật ngữ) được đọc bằng cách cắt khối `const x = [...]` rồi đánh giá như JS.
import { readFileSync } from 'node:fs'
import { execFileSync } from 'node:child_process'
import { join } from 'node:path'
import vm from 'node:vm'

const client = process.argv[2]
if (!client) {
  console.error('Dùng: export-content.mjs <thư-mục-repo-client>')
  process.exit(2)
}

const { solutionCategories, solutionSlug } = await import(join(client, 'src/data/solutions.ts'))
const { articles } = await import(join(client, 'src/data/articles.ts'))
const { resourceSections } = await import(join(client, 'src/data/resources.ts'))

const resourceView = readFileSync(join(client, 'src/views/ResourceView.vue'), 'utf8')
function arrayLiteral(name) {
  const start = resourceView.indexOf(`const ${name} = [`)
  if (start < 0) throw new Error(`Không thấy const ${name} trong ResourceView.vue`)
  let depth = 0
  for (let i = resourceView.indexOf('[', start); i < resourceView.length; i++) {
    if (resourceView[i] === '[') depth++
    if (resourceView[i] === ']' && --depth === 0) return vm.runInNewContext(`(${resourceView.slice(resourceView.indexOf('[', start), i + 1)})`)
  }
  throw new Error(`Mảng ${name} không đóng`)
}

let blockSeq = 0
const id = () => `b${++blockSeq}`
const para = (text) => ({ id: id(), type: 'paragraph', spans: [{ text }] })
const list = (items, ordered = false) => ({ id: id(), type: 'list', ordered, items: items.map((t) => [{ text: t }]) })
const heading = (text, level = 2) => ({ id: id(), type: 'heading', level, text })
const callout = (text, tone = 'info') => ({ id: id(), type: 'callout', tone, spans: [{ text }] })
const cta = (label, href) => ({ id: id(), type: 'cta', label, href })
const stripDot = (s) => s.trim().replace(/\.$/, '')

const topics = []
const posts = []
function post(p) {
  blockSeq = 0
  const blocks = p.blocks()
  posts.push({ ...p, blocks })
}

// Giải pháp / đào tạo: /giai-phap/<nhóm>/<trang>/<hạng-mục>
solutionCategories.forEach((cat, ci) => {
  const isTraining = cat.name === 'Đào Tạo'
  const type = isTraining ? 'training' : 'solution'
  const catSlug = solutionSlug(cat.name)
  topics.push({ key: `${type}:${catSlug}`, postType: type, parentKey: null, slug: catSlug, name: cat.name,
    description: cat.description, imageUrl: cat.image, sortOrder: ci })
  cat.subcategories.forEach((sub, si) => {
    const subSlug = solutionSlug(sub.name)
    post({ postType: type, topicKey: `${type}:${catSlug}`, parentSlug: null, slug: subSlug, title: sub.name,
      excerpt: sub.description ?? null, label: null, keyPoints: sub.highlights ?? sub.focusAreas ?? [], sortOrder: si,
      blocks: () => [
        ...(sub.description ? [para(sub.description)] : []),
        ...(sub.focusAreas?.length ? [heading('Nội dung chính'), list(sub.focusAreas)] : []),
        ...(sub.items.length ? [heading('Hạng mục'), list(sub.items.map(stripDot))] : []),
        ...(sub.audience ? [callout(`Đối tượng phù hợp: ${sub.audience}`)] : []),
      ] })
    sub.items.forEach((item, ii) => {
      post({ postType: type, topicKey: `${type}:${catSlug}`, parentSlug: subSlug, slug: solutionSlug(item), title: stripDot(item),
        excerpt: null, label: null, keyPoints: [], sortOrder: ii, blocks: () => [para(item.trim())] })
    })
  })
})

// Tin tức: /tin-tuc/<slug>
const toIso = (d) => { const [dd, mm, yyyy] = d.split('.'); return `${yyyy}-${mm}-${dd}T09:00:00+07:00` }
articles.forEach((a, ai) => {
  post({ postType: 'news', topicKey: null, parentSlug: null, slug: a.slug, title: a.title, excerpt: a.excerpt, label: a.label,
    publishedAt: toIso(a.date), readingMinutes: parseInt(a.readTime, 10) || null,
    coverImageUrl: a.imageUrl, coverImageAlt: a.imageAlt, keyPoints: a.keyPoints, sortOrder: ai, isFeatured: ai === 0,
    blocks: () => a.sections.flatMap((s) => [heading(s.heading), ...s.paragraphs.map(para), ...(s.bullets?.length ? [list(s.bullets)] : [])]) })
})

// Tài nguyên: /tai-nguyen/<mục>/<chủ-đề>
resourceSections.forEach((sec, i) => {
  topics.push({ key: `resource:${sec.slug}`, postType: 'resource', parentKey: null, slug: sec.slug, name: sec.name,
    description: sec.description, imageUrl: sec.image, sortOrder: i })
  sec.topics.forEach((t, j) => topics.push({ key: `resource:${sec.slug}/${t.slug}`, postType: 'resource',
    parentKey: `resource:${sec.slug}`, slug: t.slug, name: t.name, description: t.description, imageUrl: null, sortOrder: j }))
})
arrayLiteral('books').forEach((b, i) => post({ postType: 'resource', topicKey: 'resource:tai-lieu/sach', parentSlug: null,
  slug: solutionSlug(b.title), title: b.title, excerpt: b.detail, label: b.label, keyPoints: [], sortOrder: i,
  blocks: () => [para(`Tác giả: ${b.author} · ${b.pages}`), para(b.detail), cta('Xem sách', b.link)] }))
// Bỏ số lượt tải / dung lượng file: số liệu minh hoạ, không phải dữ liệu thật.
arrayLiteral('downloads').forEach((d, i) => post({ postType: 'resource', topicKey: 'resource:tai-lieu/download', parentSlug: null,
  slug: solutionSlug(d.title), title: d.title, excerpt: d.detail, label: d.label, keyPoints: [], sortOrder: i,
  blocks: () => [para(d.detail), cta('Mở tài liệu', d.link)] }))
arrayLiteral('glossary').forEach((g, i) => post({ postType: 'glossary', topicKey: null, parentSlug: null,
  slug: solutionSlug(g.term), title: g.term, excerpt: g.description, label: g.category, keyPoints: [], sortOrder: i,
  blocks: () => [para(g.fullName), para(g.description)] }))

let commit = null
try { commit = execFileSync('git', ['-C', client, 'rev-parse', '--short', 'HEAD'], { encoding: 'utf8' }).trim() } catch { /* không phải repo git */ }

process.stdout.write(JSON.stringify({ version: 1, source: commit ? `DXTechs@${commit}` : 'DXTechs', topics, posts }, null, 1) + '\n')
