import Quality from 'Quality/Quality';
import { QualityProfileFormatItem } from './CustomFormat';

export interface QualityProfileQualityItem {
  id?: number;
  quality?: Quality;
  items: QualityProfileQualityItem[];
  allowed: boolean;
  name?: string;
}

interface QualityProfile {
  isComposite?: boolean;
  name: string;
  upgradeAllowed: boolean;
  cutoff: number;
  items: QualityProfileQualityItem[];
  minFormatScore: number;
  cutoffFormatScore: number;
  minUpgradeFormatScore: number;
  formatItems: QualityProfileFormatItem[];
  sizeDefinitions?:
    | {
        qualityId: number;
        minSize: number | null;
        preferredSize: number | null;
        maxSize: number | null;
      }[]
    | null;
  useGlobalSizeDefinitions?: boolean;
  id: number;
}

export default QualityProfile;
