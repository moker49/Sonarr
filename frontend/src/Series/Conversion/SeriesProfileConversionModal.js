import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import Alert from 'Components/Alert';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import Button from 'Components/Link/Button';
import SpinnerButton from 'Components/Link/SpinnerButton';
import Modal from 'Components/Modal/Modal';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import { inputTypes, kinds, sizes } from 'Helpers/Props';
import { fetchEpisodes } from 'Store/Actions/episodeActions';
import { fetchEpisodeFiles } from 'Store/Actions/episodeFileActions';
import { fetchSeries } from 'Store/Actions/seriesActions';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import formatBytes from 'Utilities/Number/formatBytes';
import translate from 'Utilities/String/translate';
import styles from './SeriesProfileConversionModal.css';

class SeriesProfileConversionModal extends Component {
  constructor(props, context) {
    super(props, context);
    this.state = {
      profiles: [],
      versions: [],
      profileId: '',
      targetId: '',
      plan: null,
      busy: true,
      error: null,
      alreadyComposite: false
    };
    this._mounted = false;
    this._requests = [];
  }

  componentDidMount() {
    this._mounted = true;
    this.request({ url: '/qualityprofile' }, (profiles) => {
      this.setState({
        profiles: profiles.filter((p) => p.isComposite),
        alreadyComposite: profiles.some((p) => p.id === this.props.qualityProfileId && p.isComposite)
      });
    });
  }

  componentWillUnmount() {
    this._mounted = false;
    this._requests.forEach((abort) => abort());
  }

  request = (options, success) => {
    const { request, abortRequest } = createAjaxRequest({ dataType: 'json', ...options });
    this._requests.push(abortRequest);
    request.done((data) => {
      if (this._mounted) {
        success(data);
      }
    }).fail((xhr) => {
      if (this._mounted && !xhr.aborted) {
        this.setState({ error: xhr.responseJSON?.message || translate('SeriesConversionRequestFailed'), plan: null });
      }
    }).always(() => {
      if (this._mounted) {
        this.setState({ busy: false });
      }
    });
  };

  onClose = () => {
    if (!this.state.busy) {
      this.props.onModalClose();
    }
  };

  onProfileChange = ({ value }) => {
    this.setState({ profileId: value, targetId: '', versions: [], plan: null, error: null, busy: !!value });
    if (value) {
      this.request({ url: `/qualityprofile/${value}/composite` }, (data) => this.setState({ versions: data.versions }));
    }
  };

  onTargetChange = ({ value }) => this.setState({ targetId: value, plan: null, error: null });

  conversionRequest = () => ({
    compositeProfileId: Number(this.state.profileId),
    existingFileVersionId: this.state.targetId ? Number(this.state.targetId) : null
  });

  onPreview = () => {
    this.setState({ busy: true, error: null, plan: null });
    this.request({
      url: `/series/${this.props.seriesId}/profileconversion/preview`,
      method: 'POST',
      data: JSON.stringify(this.conversionRequest())
    }, (plan) => this.setState({ plan }));
  };

  onApply = () => {
    const { plan } = this.state;
    if (!plan) {
      return;
    }
    this.setState({ busy: true, error: null });
    this.request({
      url: `/series/${this.props.seriesId}/profileconversion`,
      method: 'POST',
      data: JSON.stringify({ ...this.conversionRequest(), snapshotToken: plan.snapshotToken })
    }, () => {
      this.props.onConverted(this.props.seriesId);
      this.props.onModalClose();
    });
  };

  render() {
    const { hasEpisodeFiles } = this.props;
    const { profiles, versions, profileId, targetId, plan, busy, error, alreadyComposite } = this.state;
    const valid = profileId && versions.length === 2 && (!hasEpisodeFiles || targetId);

    return (
      <Modal
        isOpen={true}
        size={sizes.LARGE}
        onModalClose={this.onClose}
      >
        <ModalContent onModalClose={this.onClose}>
          <ModalHeader>{translate('ConvertSeriesProfile')}</ModalHeader>
          <ModalBody>
            {
              error &&
                <Alert kind={kinds.DANGER}>{error}</Alert>
            }
            {
              alreadyComposite &&
                <Alert>{translate('SeriesConversionAlreadyComposite')}</Alert>
            }
            {
              !alreadyComposite &&
                <div>
                  <p>{translate('SeriesConversionDescription')}</p>
                  {
                    !busy && profiles.length === 0 &&
                      <Alert>{translate('SeriesConversionNoProfiles')}</Alert>
                  }
                  <FormGroup>
                    <FormLabel>{translate('SeriesConversionCompositeProfile')}</FormLabel>
                    <FormInputGroup
                      type={inputTypes.SELECT}
                      name="profileId"
                      value={profileId}
                      values={[{ key: '', value: translate('SeriesConversionSelect') }, ...profiles.map((p) => ({ key: String(p.id), value: p.name }))]}
                      isDisabled={busy}
                      onChange={this.onProfileChange}
                    />
                  </FormGroup>
                  <FormGroup>
                    <FormLabel>{translate('SeriesConversionExistingFiles')}</FormLabel>
                    <FormInputGroup
                      type={inputTypes.SELECT}
                      name="targetId"
                      value={targetId}
                      values={[{ key: '', value: translate('SeriesConversionSelect') }, ...versions.map((v) => ({ key: String(v.id), value: v.isPrimary ? `${v.label} (${translate('SeriesConversionPrimary')})` : v.label }))]}
                      isDisabled={busy || versions.length === 0}
                      onChange={this.onTargetChange}
                    />
                  </FormGroup>
                  {
                    plan &&
                      <div>
                        <h3>{translate('SeriesConversionReview')}</h3>
                        <p>{translate('SeriesConversionReviewSummary', { episodes: plan.episodeIds.length, files: plan.existingFiles.length })}</p>
                        <ul>
                          {
                            plan.versions.map((v) => (
                              <li key={v.id}>
                                {v.label}{v.isPrimary ? ` (${translate('SeriesConversionPrimary')})` : ''}: {translate('SeriesConversionFileCount', { count: plan.existingFiles.filter((f) => f.compositeProfileVersionId === v.id).length })}
                              </li>
                            ))
                          }
                        </ul>
                        <ul className={styles.files}>
                          {
                            plan.existingFiles.map((f) => (
                              <li key={f.episodeFileId}>
                                <span className={styles.path}>{f.relativePath}</span> — {translate('SeriesConversionFileDetails', { episodes: f.episodeIds.length, size: formatBytes(f.size) })}
                              </li>
                            ))
                          }
                        </ul>
                        <Alert kind={kinds.WARNING}>{translate('SeriesConversionApplyNotice')}</Alert>
                      </div>
                  }
                </div>
            }
          </ModalBody>
          <ModalFooter>
            <Button isDisabled={busy} onPress={this.onClose}>{translate('Cancel')}</Button>
            {
              !alreadyComposite && !plan &&
                <SpinnerButton
                  isSpinning={busy}
                  isDisabled={!valid || busy}
                  onPress={this.onPreview}
                >
                  {translate('SeriesConversionPreview')}
                </SpinnerButton>
            }
            {
              !alreadyComposite && plan &&
                <SpinnerButton
                  kind={kinds.PRIMARY}
                  isSpinning={busy}
                  isDisabled={busy}
                  onPress={this.onApply}
                >
                  {translate('SeriesConversionApply')}
                </SpinnerButton>
            }
          </ModalFooter>
        </ModalContent>
      </Modal>
    );
  }
}

SeriesProfileConversionModal.propTypes = {
  seriesId: PropTypes.number.isRequired,
  qualityProfileId: PropTypes.number.isRequired,
  hasEpisodeFiles: PropTypes.bool.isRequired,
  onModalClose: PropTypes.func.isRequired,
  onConverted: PropTypes.func.isRequired
};

export default connect(null, (dispatch) => ({
  onConverted: (seriesId) => {
    dispatch(fetchSeries({ id: seriesId }));
    dispatch(fetchEpisodes({ seriesId }));
    dispatch(fetchEpisodeFiles({ seriesId }));
  }
}))(SeriesProfileConversionModal);
