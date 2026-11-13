import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Card from 'Components/Card';
import Label from 'Components/Label';
import IconButton from 'Components/Link/IconButton';
import ConfirmModal from 'Components/Modal/ConfirmModal';
import Tooltip from 'Components/Tooltip/Tooltip';
import { icons, kinds, tooltipPositions } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import CompositeProfileModal from './CompositeProfileModal';
import EditQualityProfileModalConnector from './EditQualityProfileModalConnector';
import styles from './QualityProfile.css';

class QualityProfile extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      isEditQualityProfileModalOpen: false,
      isCompositeProfileModalOpen: false,
      isDeleteQualityProfileModalOpen: false
    };
  }

  //
  // Listeners

  onEditQualityProfilePress = () => {
    this.setState(this.props.isComposite ? { isCompositeProfileModalOpen: true } : { isEditQualityProfileModalOpen: true });
  };

  onCompositeProfilePress = () => this.setState({ isCompositeProfileModalOpen: true });

  onCompositeProfileClose = () => this.setState({ isCompositeProfileModalOpen: false });

  onProfileSettingsPress = () => this.setState({ isEditQualityProfileModalOpen: true });

  onEditQualityProfileModalClose = () => {
    this.setState({ isEditQualityProfileModalOpen: false });
  };

  onDeleteQualityProfilePress = () => {
    this.setState({
      isEditQualityProfileModalOpen: false,
      isDeleteQualityProfileModalOpen: true
    });
  };

  onDeleteQualityProfileModalClose = () => {
    this.setState({ isDeleteQualityProfileModalOpen: false });
  };

  onConfirmDeleteQualityProfile = () => {
    this.props.onConfirmDeleteQualityProfile(this.props.id);
  };

  onCloneQualityProfilePress = () => {
    const {
      id,
      onCloneQualityProfilePress
    } = this.props;

    onCloneQualityProfilePress(id);
  };

  //
  // Render

  render() {
    const {
      id,
      name,
      isComposite,
      upgradeAllowed,
      cutoff,
      items,
      isDeleting
    } = this.props;

    return (
      <Card
        className={styles.qualityProfile}
        overlayContent={true}
        onPress={this.onEditQualityProfilePress}
      >
        <div className={styles.nameContainer}>
          <div className={styles.name}>
            {name}
            {isComposite ? ` (${translate('CompositeEditorBadge')})` : null}
          </div>

          <IconButton
            className={styles.cloneButton}
            title={translate('CompositeEditorConfigure')}
            name={icons.PROFILE}
            onPress={this.onCompositeProfilePress}
          />

          <IconButton
            className={styles.cloneButton}
            title={translate(isComposite ? 'EditQualityProfile' : 'CloneProfile')}
            name={isComposite ? icons.EDIT : icons.CLONE}
            onPress={isComposite ? this.onProfileSettingsPress : this.onCloneQualityProfilePress}
          />
        </div>

        <div className={styles.qualities}>
          {
            !isComposite && items.map((item) => {
              if (!item.allowed) {
                return null;
              }

              if (item.quality) {
                const isCutoff = upgradeAllowed && item.quality.id === cutoff;

                return (
                  <Label
                    key={item.quality.id}
                    kind={isCutoff ? kinds.INFO : kinds.DEFAULT}
                    title={isCutoff ? translate('UpgradeUntilThisQualityIsMetOrExceeded') : null}
                  >
                    {item.quality.name}
                  </Label>
                );
              }

              const isCutoff = upgradeAllowed && item.id === cutoff;

              return (
                <Tooltip
                  key={item.id}
                  className={styles.tooltipLabel}
                  anchor={
                    <Label
                      kind={isCutoff ? kinds.INFO : kinds.DEFAULT}
                      title={isCutoff ? translate('Cutoff') : null}
                    >
                      {item.name}
                    </Label>
                  }
                  tooltip={
                    <div>
                      {
                        item.items.map((groupItem) => {
                          return (
                            <Label
                              key={groupItem.quality.id}
                              kind={isCutoff ? kinds.INFO : kinds.DEFAULT}
                              title={isCutoff ? translate('Cutoff') : null}
                            >
                              {groupItem.quality.name}
                            </Label>
                          );
                        })
                      }
                    </div>
                  }
                  kind={kinds.INVERSE}
                  position={tooltipPositions.TOP}
                />
              );
            })
          }
        </div>

        {
          isComposite &&
            <p>{translate('CompositeEditorCardHelp')}</p>
        }

        {
          this.state.isCompositeProfileModalOpen &&
            <CompositeProfileModal
              id={id}
              name={name}
              onModalClose={this.onCompositeProfileClose}
            />
        }

        <EditQualityProfileModalConnector
          id={id}
          isOpen={this.state.isEditQualityProfileModalOpen}
          onModalClose={this.onEditQualityProfileModalClose}
          onDeleteQualityProfilePress={this.onDeleteQualityProfilePress}
        />

        <ConfirmModal
          isOpen={this.state.isDeleteQualityProfileModalOpen}
          kind={kinds.DANGER}
          title={translate('DeleteQualityProfile')}
          message={translate('DeleteQualityProfileMessageText', { name })}
          confirmLabel={translate('Delete')}
          isSpinning={isDeleting}
          onConfirm={this.onConfirmDeleteQualityProfile}
          onCancel={this.onDeleteQualityProfileModalClose}
        />
      </Card>
    );
  }
}

QualityProfile.propTypes = {
  id: PropTypes.number.isRequired,
  name: PropTypes.string.isRequired,
  isComposite: PropTypes.bool,
  upgradeAllowed: PropTypes.bool.isRequired,
  cutoff: PropTypes.number.isRequired,
  items: PropTypes.arrayOf(PropTypes.object).isRequired,
  isDeleting: PropTypes.bool.isRequired,
  onConfirmDeleteQualityProfile: PropTypes.func.isRequired,
  onCloneQualityProfilePress: PropTypes.func.isRequired
};

export default QualityProfile;
