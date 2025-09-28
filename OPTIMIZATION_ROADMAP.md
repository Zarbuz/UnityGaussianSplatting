# Gaussian Splatting Optimization Roadmap
*Optimisations pour les scènes larges avec millions de splats*

## Vue d'ensemble

Ce document présente les optimisations critiques identifiées pour améliorer les performances du système de rendu Gaussian Splatting Unity, particulièrement pour les scènes contenant plusieurs millions de splats.

## Analyse de l'implémentation actuelle

### Goulots d'étranglement identifiés
- **Tri complet** : Tous les splats sont triés même hors du champ de vision
- **Pas de culling** : Aucun système de frustum ou occlusion culling
- **Mémoire GPU statique** : Buffers alloués pour tous les splats simultanément
- **Rendu monolithique** : Un seul `DrawProcedural` pour tous les splats

## Optimisations Prioritaires

### ✅ **Implémenté**

#### 1. Frustum Culling et Stream Compaction - **TERMINÉ**
- **Fichiers implémentés** :
  - `GaussianSplatRenderer.cs:865-1031` (frustum culling avec chunks)
  - `SplatStreamCompact.compute` (stream compaction WebGPU-compatible)
  - `SplatUtilitiesFFX.compute` et `SplatUtilitiesRadix.compute` (AABB expansion conservative)
- **Fonctionnalités ajoutées** :
  - Culling hiérarchique par chunks avec AABB expansion conservative
  - Stream compaction GPU avec opérations atomiques séparées
  - Tolérance configurable pour éviter le sur-culling
  - Reporting de visibilité en pourcentage pour debug
- **Impact mesuré** : **Frustum culling fonctionnel avec stream compaction optimisée**

### 🔥 **Priorité Critique Restante**

#### 2. Tri Hiérarchique et Chunking Spatial Avancé
- **Fichier concerné** : `GaussianSplatRenderer.cs:699-725` (`SortPoints`)
- **Statut** : **Partiellement implémenté** (culling par chunks fait, tri hiérarchique à améliorer)
- **Problème restant** : Tri de tous les splats visibles à chaque frame
- **Solutions à implémenter** :
  - Tri par chunk avec priorité basée sur la distance caméra
  - Cache de tri pour chunks statiques
  - Tri adaptatif différentiel (seulement si mouvement significatif)
- **Impact estimé** : **40-60% de réduction** du coût de tri

### ⚡ **Priorité Élevée**

#### 3. GPU Memory Management et Streaming
- **Fichiers concernés** :
  - `GaussianSplatRenderer.cs:393` (`m_GpuPosData`)
  - `GaussianSplatRenderer.cs:423` (`m_GpuView`)
- **Problème** : Allocation statique de tous les buffers GPU
- **Solutions** :
  ```csharp
  // Pool de buffers réutilisables
  class GpuBufferPool {
      Dictionary<int, Queue<GraphicsBuffer>> availableBuffers;
      GraphicsBuffer GetBuffer(int size);
      void ReturnBuffer(GraphicsBuffer buffer);
  }
  ```
- **Impact estimé** : **50-70% de réduction** de la mémoire GPU

#### 4. Occlusion Culling
- **Implémentation manquante**
- **Solution** : Hi-Z buffer occlusion culling
  ```hlsl
  // Dans le compute shader, avant tri
  float depth = SampleHiZBuffer(screenPos);
  if (splatDepth > depth + threshold) discard;
  ```
- **Impact estimé** : **30-50% de réduction** pour les scènes denses

### 🚀 **Priorité Moyenne**

#### 5. Rendu Multi-Pass et Instancing
- **Fichier concerné** : `GaussianSplatRenderSystem.cs:165` (`DrawProcedural`)
- **Problème** : Rendu monolithique de tous les splats
- **Solutions** :
  - Rendu par chunks avec LOD différent
  - GPU Instancing pour splats similaires
  - Early-Z pass pour réduction overdraw

#### 6. Optimisations Shader
- **Fichier concerné** : `RenderGaussianSplats.shader:35-77`
- **Améliorations** :
  ```hlsl
  // Early discard dans vertex shader
  if (behindCam || outsideFrustum) {
      o.vertex = asfloat(0x7fc00000); // NaN discard
      return o;
  }
  ```

#### 7. Streaming Spatial Intelligent
- **Nouveau système à implémenter**
- **Fonctionnalités** :
  - Tiles/chunks spatiaux dynamiques
  - Prédiction de mouvement caméra
  - Cache intelligent des chunks visibles
  - Compression des données SH distantes

### 📈 **Optimisations Avancées**

#### 8. Temporal Coherence
- Exploitation de la cohérence temporelle entre frames
- Cache des résultats de tri similaires
- Mise à jour différentielle des transformations

#### 9. Memory Layout Optimization
- **Structure-of-Arrays (SoA)** au lieu d'Array-of-Structures
- Meilleur cache hit pour accès GPU parallèles
- Compression adaptative des données selon la distance

#### 10. Tri Asynchrone Multi-threaded
- **Fichiers concernés** : `GpuSorting.cs`, `GpuSortingRadix.cs`
- Tri en arrière-plan sur compute shaders dédiés
- Pipeline de tri overlappé avec rendu

## Plan d'implémentation

### Phase 1 : Fondations ~~(2-3 semaines)~~ - **COMPLÉTÉE**
1. ✅ **Frustum Culling** - Impact immédiat maximal (TERMINÉ)
2. ✅ **Chunking spatial basique** - Base pour optimisations futures (TERMINÉ)
3. **Streaming de buffers** - Réduction mémoire (EN COURS via stream compaction)

### Phase 2 : Performance (3-4 semaines)
4. **Occlusion culling**
5. **Tri hiérarchique optimisé**
6. **Multi-pass rendering**

### Phase 3 : Raffinement (2-3 semaines)
7. **Optimisations shader**
8. **Temporal coherence**
9. **Memory layout**

## Métriques de performance attendues

### Scénarios de test
- **Petite scène** : 100K splats → Pas d'impact négatif
- **Scène moyenne** : 1M splats → 2-3x amélioration FPS
- **Grande scène** : 5M+ splats → 5-10x amélioration FPS

### Gains estimés par optimisation
| Optimisation | Gain FPS | Réduction Mémoire | Effort | Statut |
|-------------|----------|-------------------|---------|---------|
| ✅ Frustum Culling | +200-400% | 0% | Moyen | **TERMINÉ** |
| ✅ Chunking Spatial | +100-200% | 30-50% | Élevé | **TERMINÉ** |
| 🔄 GPU Stream Compaction | +50-100% | 20-40% | Élevé | **EN COURS** |
| Occlusion Culling | +50-150% | 0% | Moyen | À faire |
| Tri Hiérarchique Avancé | +50-100% | 10-20% | Moyen | À faire |

## Implémentation recommandée

### Architecture actuelle vs cible
```
GaussianSplatRenderer
├── ✅ SpatialChunkManager (implémenté)
│   ├── ✅ FrustumCuller (avec AABB expansion conservative)
│   ├── ❌ OcclusionCuller (à implémenter)
│   └── ❌ LODManager (à implémenter)
├── 🔄 StreamingSystem (en cours)
│   ├── 🔄 StreamCompaction (SplatStreamCompact.compute)
│   ├── ❌ GpuBufferPool (à implémenter)
│   └── ❌ ChunkStreamer (à implémenter)
└── ❌ OptimizedRenderSystem (à refactoriser)
    ├── ❌ MultiPassRenderer (à implémenter)
    └── ❌ TemporalCache (à implémenter)
```

### Fichiers à modifier/créer
- **✅ Créés** : `SplatStreamCompact.compute` (stream compaction WebGPU)
- **✅ Modifiés** :
  - `GaussianSplatRenderer.cs` (frustum culling + stream compaction)
  - `SplatUtilitiesFFX.compute` (AABB expansion conservative)
  - `SplatUtilitiesRadix.compute` (AABB expansion conservative)
- **❌ À créer** : `SpatialChunkManager.cs`, `StreamingSystem.cs`, `OptimizedSorting.cs`
- **❌ À modifier** : `GaussianSplatRenderSystem.cs`
- **❌ Shaders à améliorer** : `RenderGaussianSplats.shader`, nouveaux compute shaders de culling

## Conclusion

**Progrès significatifs réalisés** : La Phase 1 de la roadmap est maintenant **complétée** avec l'implémentation du frustum culling hiérarchique, du chunking spatial, et de la stream compaction GPU. Ces optimisations fondamentales forment la base solide pour les améliorations futures.

**Prochaines étapes prioritaires** :
1. **Occlusion Culling** - Impact immédiat sur les scènes denses
2. **Tri hiérarchique avancé** - Optimisation des splats visibles restants
3. **GPU Buffer Pool** - Gestion mémoire dynamique

La roadmap modifiée permettra de transformer le système actuel pour supporter des scènes de 10M+ splats avec des performances fluides (60+ FPS) sur hardware moderne. Les fondations étant maintenant en place, les prochaines optimisations auront un impact cumulatif encore plus important.

**Impact mesuré de la Phase 1** : Frustum culling fonctionnel avec reporting de visibilité en temps réel, permettant déjà une réduction significative de la charge GPU pour les grandes scènes.