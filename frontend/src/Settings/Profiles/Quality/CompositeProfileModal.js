import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import Alert from 'Components/Alert';
import FieldSet from 'Components/FieldSet';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import Button from 'Components/Link/Button';
import SpinnerButton from 'Components/Link/SpinnerButton';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import ConfirmModal from 'Components/Modal/ConfirmModal';
import Modal from 'Components/Modal/Modal';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import { inputTypes, kinds, sizes } from 'Helpers/Props';
import { fetchQualityProfiles } from 'Store/Actions/settingsActions';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import translate from 'Utilities/String/translate';

class CompositeProfileModal extends Component {
  constructor(props, context) {
    super(props, context);
    this.state = {
      profiles: [],
      versions: [],
      loading: true,
      busy: false,
      error: null,
      hasComposite: false,
      confirmingRemoval: false
    };
    this._mounted = false;
    this._requests = [];
  }

  componentDidMount() {
    this._mounted = true;
    this.request({ url: '/qualityprofile' }, (profiles) => {
      this.setState({ profiles: profiles.filter((p) => !p.isComposite && p.id !== this.props.id) });
      this.request({ url: `/qualityprofile/${this.props.id}/composite` }, ({ versions }) => {
        this.setState({
          loading: false,
          hasComposite: versions.length > 0,
          versions: versions.length > 0 ? versions : [
            { id: 0, qualityProfileId: '', label: '', priority: 0, isPrimary: true },
            { id: 0, qualityProfileId: '', label: '', priority: 1, isPrimary: false }
          ]
        });
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
        const response = xhr.responseJSON;
        const message = Array.isArray(response) ? response.map((e) => e.errorMessage).filter(Boolean).join(' ') : response?.message;
        this.setState({ error: message || translate('CompositeEditorRequestFailed'), loading: false });
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

  onVersionChange = (index, { name, value }) => {
    this.setState(({ versions }) => ({
      versions: versions.map((v, i) => (i === index ? { ...v, [name]: value } : v)),
      error: null
    }));
  };

  onPrimaryChange = ({ value }) => {
    this.setState(({ versions }) => ({
      versions: versions.map((v, i) => ({ ...v, isPrimary: i === Number(value) })),
      error: null
    }));
  };

  onSaved = () => {
    this.props.dispatchFetchQualityProfiles();
    this.props.onModalClose();
  };

  onSave = () => {
    this.setState({ busy: true, error: null });
    this.request({
      url: `/qualityprofile/${this.props.id}/composite`,
      method: 'PUT',
      data: JSON.stringify({ versions: this.state.versions.map((v, priority) => ({ ...v, qualityProfileId: Number(v.qualityProfileId), priority })) })
    }, this.onSaved);
  };

  onRemovePress = () => this.setState({ confirmingRemoval: true });

  onRemoveCancel = () => {
    if (!this.state.busy) {
      this.setState({ confirmingRemoval: false });
    }
  };

  onRemove = () => {
    this.setState({ busy: true, error: null, confirmingRemoval: false });
    this.request({ url: `/qualityprofile/${this.props.id}/composite`, method: 'DELETE' }, this.onSaved);
  };

  render() {
    const { name } = this.props;
    const { profiles, versions, loading, busy, error, hasComposite, confirmingRemoval } = this.state;
    const valid = versions.length === 2 && versions.every((v) => v.qualityProfileId && v.label.trim()) &&
      Number(versions[0].qualityProfileId) !== Number(versions[1].qualityProfileId) &&
      versions[0].label.toLowerCase() !== versions[1].label.toLowerCase();
    const values = [{ key: '', value: translate('SeriesConversionSelect') }, ...profiles.map((p) => ({ key: String(p.id), value: p.name }))];

    return (
      <Modal
        isOpen={true}
        size={sizes.LARGE}
        onModalClose={this.onClose}
      >
        <ModalContent onModalClose={this.onClose}>
          <ModalHeader>{translate('CompositeEditorTitle', { name })}</ModalHeader>
          <ModalBody>
            <p>{translate('CompositeEditorDescription')}</p>
            {
              loading &&
                <LoadingIndicator />
            }
            {
              error &&
                <Alert kind={kinds.DANGER}>{error}</Alert>
            }
            {
              !loading && profiles.length < 2 &&
                <Alert kind={kinds.WARNING}>{translate('CompositeEditorNeedsProfiles')}</Alert>
            }
            {
              !loading && versions.map((version, index) => (
                <FieldSet key={index} legend={translate('CompositeEditorVersion', { number: index + 1 })}>
                  <FormGroup>
                    <FormLabel>{translate('QualityProfile')}</FormLabel>
                    <FormInputGroup
                      type={inputTypes.SELECT}
                      name="qualityProfileId"
                      value={String(version.qualityProfileId)}
                      values={values}
                      isDisabled={busy}
                      onChange={(change) => this.onVersionChange(index, change)}
                    />
                  </FormGroup>
                  <FormGroup>
                    <FormLabel>{translate('CompositeEditorLabel')}</FormLabel>
                    <FormInputGroup
                      type={inputTypes.TEXT}
                      name="label"
                      value={version.label}
                      maxLength={64}
                      isDisabled={busy}
                      helpText={translate('CompositeEditorLabelHelp')}
                      onChange={(change) => this.onVersionChange(index, change)}
                    />
                  </FormGroup>
                </FieldSet>
              ))
            }
            {
              !loading && versions.length === 2 &&
                <FormGroup>
                  <FormLabel>{translate('CompositeEditorPrimary')}</FormLabel>
                  <FormInputGroup
                    type={inputTypes.SELECT}
                    name="primary"
                    value={String(versions.findIndex((v) => v.isPrimary))}
                    values={versions.map((v, i) => ({ key: String(i), value: v.label || translate('CompositeEditorVersion', { number: i + 1 }) }))}
                    isDisabled={busy}
                    helpText={translate('CompositeEditorPrimaryHelp')}
                    onChange={this.onPrimaryChange}
                  />
                </FormGroup>
            }
            <Alert>{translate('CompositeEditorUsageHelp')}</Alert>
          </ModalBody>
          <ModalFooter>
            {
              hasComposite &&
                <Button
                  kind={kinds.DANGER}
                  isDisabled={loading || busy}
                  onPress={this.onRemovePress}
                >
                  {translate('CompositeEditorRemove')}
                </Button>
            }
            <Button isDisabled={busy} onPress={this.onClose}>{translate('Cancel')}</Button>
            <SpinnerButton
              kind={kinds.PRIMARY}
              isSpinning={busy}
              isDisabled={loading || busy || !valid}
              onPress={this.onSave}
            >
              {translate('Save')}
            </SpinnerButton>
          </ModalFooter>
          <ConfirmModal
            isOpen={confirmingRemoval}
            kind={kinds.DANGER}
            title={translate('CompositeEditorRemove')}
            message={translate('CompositeEditorRemoveHelp')}
            confirmLabel={translate('CompositeEditorRemove')}
            isSpinning={busy}
            onConfirm={this.onRemove}
            onCancel={this.onRemoveCancel}
          />
        </ModalContent>
      </Modal>
    );
  }
}

CompositeProfileModal.propTypes = {
  id: PropTypes.number.isRequired,
  name: PropTypes.string.isRequired,
  onModalClose: PropTypes.func.isRequired,
  dispatchFetchQualityProfiles: PropTypes.func.isRequired
};

export default connect(null, { dispatchFetchQualityProfiles: fetchQualityProfiles })(CompositeProfileModal);
